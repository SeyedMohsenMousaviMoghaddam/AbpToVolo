using System.Text;
using AbpToVolo.Core;
using AbpToVolo.Mappings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.Steps;

/// <summary>
/// Second database step of the Module Zero replacement (SQL Server): the solution's own tables keep their data.
/// In ABP.io user/tenant/role ids are Guid and the audit properties are CreatorId/LastModifierId/DeleterId, so the
/// converted model has, for every table:
///   CreatorUserId bigint → CreatorId uniqueidentifier (also LastModifier/Deleter), XUserId bigint / XTenantId int /
///   XRoleId int → uniqueidentifier.
/// Without this step the next EF Core migration would drop those columns (data loss) or fail (bigint → uniqueidentifier).
/// A generated migration (ConvertModuleZeroKeys):
///   1. fills Zero_IdMap (old id → new Guid) from the Zero_* tables — the data script copies users/roles/tenants with these ids;
///   2. converts each column through Zero_IdMap (new column, data, drop FKs to the Zero_* tables, swap, recreate indexes);
/// and the model snapshot is updated the same way, so the following migration only adds the ABP.io tables and the new FKs.
/// </summary>
internal static class ZeroForeignKeys
{
    public const string MigrationName = "ConvertModuleZeroKeys";

    internal sealed record Conversion(string Entity, string Schema, string Table, string Property, string Column, string NewProperty, string NewColumn, string Kind, bool Nullable);

    /// <summary>Columns of the solution's own entities that refer to Module Zero entities (by the ZeroMappings.GuidIdKind convention).</summary>
    internal static List<Conversion> FindConversions(CompilationUnitSyntax snapshot, Func<string, bool> isZeroEntity, IdTypeMode mode = IdTypeMode.Guid)
    {
        var blocks = EntityBlocks(snapshot).ToList();

        // entity → (schema, table); TPH derived types (HasBaseType) share the table of their base type.
        var tables = new Dictionary<string, (string Schema, string Table)>(StringComparer.Ordinal);
        var baseTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (entity, body) in blocks)
        {
            foreach (var call in Calls(body))
            {
                if (call.Name == "ToTable" && call.Args.Count >= 1) tables[entity] = (call.Args.Count > 1 ? call.Args[1] : "dbo", call.Args[0]);
                if (call.Name == "HasBaseType" && call.Args.Count == 1) baseTypes[entity] = call.Args[0];
            }
        }

        (string Schema, string Table)? TableOf(string entity)
        {
            for (var e = entity; e is not null; e = baseTypes.GetValueOrDefault(e))
            {
                if (tables.TryGetValue(e, out var t)) return t;
            }

            return null;
        }

        var result = new List<Conversion>();
        foreach (var (entity, body) in blocks)
        {
            if (isZeroEntity(entity) || TableOf(entity) is not { } table) continue;
            foreach (var statement in body.Statements.OfType<ExpressionStatementSyntax>())
            {
                if (PropertyOf(statement) is not var (type, property)) continue;
                if (ZeroMappings.GuidIdKind(property, type, mode) is not { } kind) continue;

                var column = Calls(statement).FirstOrDefault(c => c.Name == "HasColumnName")?.Args.FirstOrDefault() ?? property;
                var newProperty = TypeMappings.MemberRenames.GetValueOrDefault(property, property);
                var newColumn = column == property ? newProperty : column;
                result.Add(new Conversion(entity, table.Schema, table.Table, property, column, newProperty, newColumn, kind, type.EndsWith('?')));
            }
        }

        return result.DistinctBy(c => (c.Schema, c.Table, c.Column)).ToList();
    }

    /// <summary>
    /// The snapshot after the conversion: converted properties are Guid (and renamed), HasIndex/HasForeignKey follow the
    /// new names, and relationships from converted columns to Module Zero entities are removed (ABP.io's are added next).
    /// </summary>
    internal static string UpdateSnapshot(CompilationUnitSyntax snapshot, List<Conversion> conversions, Func<string, bool> isZeroEntity)
    {
        var byEntity = conversions.GroupBy(c => c.Entity).ToDictionary(g => g.Key, g => g.ToDictionary(c => c.Property, StringComparer.Ordinal), StringComparer.Ordinal);
        var replacements = new Dictionary<SyntaxNode, SyntaxNode?>();

        foreach (var (entity, body) in EntityBlocks(snapshot))
        {
            if (!byEntity.TryGetValue(entity, out var properties)) continue;
            var removedNavigations = new HashSet<string>(StringComparer.Ordinal);

            foreach (var statement in body.Statements.OfType<ExpressionStatementSyntax>())
            {
                var calls = Calls(statement).ToList();
                var first = calls.LastOrDefault(); // the innermost call is the first one of the chain

                // b.HasOne("Zero.User", "CreatorUser").WithMany().HasForeignKey("CreatorUserId")
                if (first is { Name: "HasOne" } && first.Args.Count >= 1 && isZeroEntity(first.Args[0]) &&
                    calls.FirstOrDefault(c => c.Name == "HasForeignKey") is { } fk && fk.Args.Any(properties.ContainsKey))
                {
                    if (first.Args.Count > 1) removedNavigations.Add(first.Args[1]);
                    replacements[statement] = null;
                    continue;
                }

                if (PropertyOf(statement) is var (type, property) && properties.TryGetValue(property, out var conversion))
                {
                    replacements[statement] = ParseStatement(ConvertedProperty(statement, conversion, type)).WithTriviaFrom(statement);
                    continue;
                }

                // HasIndex("CreatorUserId", ...) / HasKey / HasForeignKey: property names follow the renames
                if (first is { Name: "HasIndex" or "HasKey" or "HasAlternateKey" } && first.Args.Any(a => properties.TryGetValue(a, out var c) && c.NewProperty != a))
                {
                    replacements[statement] = new PropertyNameRewriter(properties).Visit(statement);
                }
            }

            foreach (var statement in body.Statements.OfType<ExpressionStatementSyntax>())
            {
                if (Calls(statement).LastOrDefault() is { Name: "Navigation" } nav && nav.Args.Count == 1 && removedNavigations.Contains(nav.Args[0]))
                {
                    replacements[statement] = null;
                }
            }
        }

        var updated = snapshot.ReplaceNodes(replacements.Keys, (original, _) => replacements[original] ?? EmptyStatement().WithLeadingTrivia(original.GetLeadingTrivia()));
        // remove the placeholders of deleted statements
        var empties = updated.DescendantNodes().OfType<EmptyStatementSyntax>().ToList();
        return updated.RemoveNodes(empties, SyntaxRemoveOptions.KeepNoTrivia)!.ToFullString();
    }

    private static string ConvertedProperty(ExpressionStatementSyntax statement, Conversion conversion, string type)
    {
        var text = statement.ToString();
        var guid = type.EndsWith('?') ? "Guid?" : "Guid";
        text = text.Replace($"Property<{type}>(\"{conversion.Property}\")", $"Property<{guid}>(\"{conversion.NewProperty}\")", StringComparison.Ordinal);
        text = text.Replace("HasColumnType(\"bigint\")", "HasColumnType(\"uniqueidentifier\")", StringComparison.Ordinal)
                   .Replace("HasColumnType(\"int\")", "HasColumnType(\"uniqueidentifier\")", StringComparison.Ordinal);
        if (conversion.Column != conversion.NewColumn || conversion.Column != conversion.Property)
        {
            text = text.Replace($"HasColumnName(\"{conversion.Column}\")", $"HasColumnName(\"{conversion.NewColumn}\")", StringComparison.Ordinal);
        }

        // UseIdentityColumn / HasDefaultValue(0) cannot apply to a Guid column
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s*\.HasDefaultValue\(\s*-?\d+L?\s*\)", "");
    }

    private sealed class PropertyNameRewriter(Dictionary<string, Conversion> properties) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitLiteralExpression(LiteralExpressionSyntax node) =>
            node.IsKind(SyntaxKind.StringLiteralExpression) && properties.TryGetValue(node.Token.ValueText, out var c)
                ? LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(c.NewProperty)).WithTriviaFrom(node)
                : node;
    }

    // ---------- snapshot parsing ----------

    private sealed record Call(string Name, List<string> Args);

    /// <summary>modelBuilder.Entity("Name", b => { ... }) blocks (an entity can have several: properties, relationships).</summary>
    private static IEnumerable<(string Entity, BlockSyntax Body)> EntityBlocks(CompilationUnitSyntax snapshot) =>
        snapshot.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Entity" } && i.ArgumentList.Arguments.Count == 2 &&
                        i.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax &&
                        i.ArgumentList.Arguments[1].Expression is SimpleLambdaExpressionSyntax { Block: not null })
            .Select(i => (((LiteralExpressionSyntax)i.ArgumentList.Arguments[0].Expression).Token.ValueText,
                ((SimpleLambdaExpressionSyntax)i.ArgumentList.Arguments[1].Expression).Block!));

    /// <summary>The calls of a fluent chain, outermost first, with their string arguments.</summary>
    private static IEnumerable<Call> Calls(SyntaxNode node) =>
        node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax)
            .Select(i => new Call(((MemberAccessExpressionSyntax)i.Expression).Name.Identifier.Text,
                i.ArgumentList.Arguments.Select(a => a.Expression).OfType<LiteralExpressionSyntax>()
                    .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)).Select(l => l.Token.ValueText).ToList()));

    /// <summary>b.Property&lt;long?&gt;("CreatorUserId")... → ("long?", "CreatorUserId").</summary>
    private static (string Type, string Property)? PropertyOf(ExpressionStatementSyntax statement)
    {
        var property = statement.DescendantNodes().OfType<InvocationExpressionSyntax>().LastOrDefault(i =>
            i.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.Text: "Property", TypeArgumentList.Arguments.Count: 1 }, Expression: IdentifierNameSyntax });
        if (property is null || property.ArgumentList.Arguments.FirstOrDefault()?.Expression is not LiteralExpressionSyntax name) return null;
        var type = ((GenericNameSyntax)((MemberAccessExpressionSyntax)property.Expression).Name).TypeArgumentList.Arguments[0].ToString();
        return (type, name.Token.ValueText);
    }

    // ---------- migration ----------

    internal static string Migration(string ns, List<Conversion> conversions, Func<string, string> zeroTable)
    {
        string Escape(string sql) => sql.Replace("\"", "\"\"");
        var exec = new StringBuilder();
        foreach (var c in conversions.OrderBy(c => c.Table, StringComparer.Ordinal).ThenBy(c => c.Column, StringComparer.Ordinal))
        {
            // Audit columns are informative (no foreign key in Boilerplate): a dangling id becomes NULL and is kept in Zero_UnmappedIds.
            var onMissing = TypeMappings.MemberRenames.ContainsKey(c.Property) ? "null" : "fail";
            exec.AppendLine($"EXEC dbo.Zero_ConvertColumnToGuid N'{c.Schema}', N'{c.Table}', N'{c.Column}', N'{c.NewColumn}', N'{c.Kind}', N'{onMissing}';");
        }

        return $$"""
            // <auto-generated>
            //   Generated by AbpToVolo. Converts the user/tenant/role foreign keys and the audit columns of the solution's
            //   own tables to the ABP.io model ({{conversions.Count}} columns): bigint/int ids → uniqueidentifier through
            //   dbo.Zero_IdMap, CreatorUserId → CreatorId, LastModifierUserId → LastModifierId, DeleterUserId → DeleterId.
            //   SQL Server only. Run it on a copy of the database first. Not reversible (Down only reports it).
            // </auto-generated>
            using System;
            using Microsoft.EntityFrameworkCore.Migrations;

            #nullable disable

            namespace {{ns}}
            {
                public partial class {{MigrationName}} : Migration
                {
                    protected override void Up(MigrationBuilder migrationBuilder)
                    {
                        migrationBuilder.Sql(@"{{Escape(IdMapSql(zeroTable))}}");

                        migrationBuilder.Sql(@"{{Escape(ConvertProcedureSql)}}");

                        migrationBuilder.Sql(@"{{Escape(exec.ToString())}}");

                        migrationBuilder.Sql(@"DROP PROCEDURE dbo.Zero_ConvertColumnToGuid;");
                    }

                    protected override void Down(MigrationBuilder migrationBuilder)
                    {
                        throw new NotSupportedException("{{MigrationName}} cannot be reverted: restore the database backup taken before it.");
                    }
                }
            }

            """;
    }

    /// <summary>Old id → new Guid for every Module Zero tenant, user, role and organization unit (idempotent).</summary>
    internal static string IdMapSql(Func<string, string> zeroTable)
    {
        var sb = new StringBuilder();
        sb.AppendLine("IF OBJECT_ID(N'dbo.Zero_IdMap') IS NULL");
        sb.AppendLine("    CREATE TABLE dbo.Zero_IdMap (EntityType nvarchar(32) NOT NULL, OldId bigint NOT NULL, NewId uniqueidentifier NOT NULL, PRIMARY KEY (EntityType, OldId));");
        sb.AppendLine("IF OBJECT_ID(N'dbo.Zero_UnmappedIds') IS NULL");
        sb.AppendLine("    CREATE TABLE dbo.Zero_UnmappedIds (TableName nvarchar(520) NOT NULL, ColumnName sysname NOT NULL, EntityType nvarchar(32) NOT NULL, OldId bigint NOT NULL, RowsCount int NOT NULL);");
        foreach (var (kind, table) in new[] { ("Tenant", "AbpTenants"), ("User", "AbpUsers"), ("Role", "AbpRoles"), ("OrganizationUnit", "AbpOrganizationUnits") })
        {
            var source = zeroTable(table);
            sb.AppendLine($"IF OBJECT_ID(N'dbo.[{source}]') IS NOT NULL");
            sb.AppendLine($"    INSERT INTO dbo.Zero_IdMap (EntityType, OldId, NewId) SELECT N'{kind}', z.Id, NEWID() FROM dbo.[{source}] z");
            sb.AppendLine($"    WHERE NOT EXISTS (SELECT 1 FROM dbo.Zero_IdMap m WHERE m.EntityType = N'{kind}' AND m.OldId = z.Id);");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Converts one column: drops its foreign keys / default constraints / indexes, adds the uniqueidentifier column
    /// filled through Zero_IdMap, drops the old column, and recreates the indexes on the new column (index names follow a rename).
    /// Ids without a match in Zero_IdMap stop the migration (the column would lose data).
    /// </summary>
    internal const string ConvertProcedureSql = """
        CREATE OR ALTER PROCEDURE dbo.Zero_ConvertColumnToGuid @Schema sysname, @Table sysname, @Column sysname, @NewColumn sysname, @EntityType nvarchar(32), @OnMissing nvarchar(10) = N'fail'
        AS
        BEGIN
            SET NOCOUNT ON;
            DECLARE @full nvarchar(520) = QUOTENAME(@Schema) + N'.' + QUOTENAME(@Table);
            DECLARE @id int = OBJECT_ID(@full);
            IF @id IS NULL OR COL_LENGTH(@full, @Column) IS NULL RETURN;
            IF EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = @id AND c.name = @Column AND TYPE_NAME(c.user_type_id) = N'uniqueidentifier') RETURN;

            DECLARE @notNull bit = (SELECT CASE WHEN c.is_nullable = 0 THEN 1 ELSE 0 END FROM sys.columns c WHERE c.object_id = @id AND c.name = @Column);
            DECLARE @drop nvarchar(max) = N'', @create nvarchar(max) = N'', @sql nvarchar(max);

            SELECT @drop += N'ALTER TABLE ' + @full + N' DROP CONSTRAINT ' + QUOTENAME(fk.name) + N';'
            FROM sys.foreign_keys fk
            WHERE fk.parent_object_id = @id
              AND EXISTS (SELECT 1 FROM sys.foreign_key_columns fc WHERE fc.constraint_object_id = fk.object_id AND COL_NAME(fc.parent_object_id, fc.parent_column_id) = @Column);

            SELECT @drop += N'ALTER TABLE ' + @full + N' DROP CONSTRAINT ' + QUOTENAME(dc.name) + N';'
            FROM sys.default_constraints dc
            WHERE dc.parent_object_id = @id AND COL_NAME(dc.parent_object_id, dc.parent_column_id) = @Column;

            SELECT
                @drop += N'DROP INDEX ' + QUOTENAME(i.name) + N' ON ' + @full + N';',
                @create += N'CREATE ' + CASE WHEN i.is_unique = 1 THEN N'UNIQUE ' ELSE N'' END + N'INDEX '
                    + QUOTENAME(REPLACE(i.name, @Column, @NewColumn)) + N' ON ' + @full + N' ('
                    + STUFF((SELECT N', ' + QUOTENAME(CASE WHEN COL_NAME(ic.object_id, ic.column_id) = @Column THEN @NewColumn ELSE COL_NAME(ic.object_id, ic.column_id) END)
                                    + CASE WHEN ic.is_descending_key = 1 THEN N' DESC' ELSE N'' END
                             FROM sys.index_columns ic WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                             ORDER BY ic.key_ordinal FOR XML PATH(''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'') + N')'
                    + ISNULL(N' INCLUDE (' + STUFF((SELECT N', ' + QUOTENAME(CASE WHEN COL_NAME(ic.object_id, ic.column_id) = @Column THEN @NewColumn ELSE COL_NAME(ic.object_id, ic.column_id) END)
                             FROM sys.index_columns ic WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1
                             FOR XML PATH(''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'') + N')', N'')
                    + ISNULL(N' WHERE ' + REPLACE(i.filter_definition, QUOTENAME(@Column), QUOTENAME(@NewColumn)), N'') + N';'
            FROM sys.indexes i
            WHERE i.object_id = @id AND i.type > 0 AND i.is_primary_key = 0 AND i.is_unique_constraint = 0
              AND EXISTS (SELECT 1 FROM sys.index_columns ic WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND COL_NAME(ic.object_id, ic.column_id) = @Column);

            EXEC sp_executesql @drop;

            DECLARE @tmp sysname = LEFT(@NewColumn, 100) + N'_AbpToVoloGuid';
            SET @sql = N'ALTER TABLE ' + @full + N' ADD ' + QUOTENAME(@tmp) + N' uniqueidentifier NULL;';
            EXEC sp_executesql @sql;

            SET @sql = N'UPDATE t SET ' + QUOTENAME(@tmp) + N' = m.NewId FROM ' + @full + N' t JOIN dbo.Zero_IdMap m ON m.EntityType = @type AND m.OldId = t.' + QUOTENAME(@Column) + N';';
            EXEC sp_executesql @sql, N'@type nvarchar(32)', @type = @EntityType;

            -- 0 is the default int/long value (Boilerplate ids start at 1): it means "no value", so it becomes the default Guid.
            SET @sql = N'UPDATE ' + @full + N' SET ' + QUOTENAME(@tmp) + N' = ''00000000-0000-0000-0000-000000000000'' WHERE ' + QUOTENAME(@Column) + N' = 0;';
            EXEC sp_executesql @sql;

            DECLARE @missing int;
            SET @sql = N'SELECT @n = COUNT(*) FROM ' + @full + N' WHERE ' + QUOTENAME(@Column) + N' IS NOT NULL AND ' + QUOTENAME(@tmp) + N' IS NULL;';
            EXEC sp_executesql @sql, N'@n int OUTPUT', @n = @missing OUTPUT;
            IF @missing > 0 AND @OnMissing = N'null'
            BEGIN
                -- informative column: the dangling ids are kept for review, the new column stays NULL for them
                SET @sql = N'INSERT INTO dbo.Zero_UnmappedIds (TableName, ColumnName, EntityType, OldId, RowsCount) SELECT @t, @c, @type, ' + QUOTENAME(@Column) + N', COUNT(*) FROM ' + @full
                    + N' WHERE ' + QUOTENAME(@Column) + N' IS NOT NULL AND ' + QUOTENAME(@tmp) + N' IS NULL GROUP BY ' + QUOTENAME(@Column) + N';';
                EXEC sp_executesql @sql, N'@t sysname, @c sysname, @type nvarchar(32)', @t = @full, @c = @Column, @type = @EntityType;
                PRINT CONCAT(@full, N'.', @Column, N': ', @missing, N' rows refer to a missing ', @EntityType, N' (set to NULL, see dbo.Zero_UnmappedIds)');
            END
            ELSE IF @missing > 0
            BEGIN
                DECLARE @message nvarchar(2048) = CONCAT(@full, N'.', @Column, N': ', @missing, N' rows refer to a ', @EntityType, N' id that does not exist (no row in Zero_IdMap). Fix or clear them, then run the migration again.');
                THROW 50001, @message, 1;
            END

            SET @sql = N'ALTER TABLE ' + @full + N' DROP COLUMN ' + QUOTENAME(@Column) + N';';
            EXEC sp_executesql @sql;

            DECLARE @rename nvarchar(1100) = @Schema + N'.' + @Table + N'.' + @tmp;
            EXEC sp_rename @rename, @NewColumn, N'COLUMN';

            IF @notNull = 1
            BEGIN
                SET @sql = N'ALTER TABLE ' + @full + N' ALTER COLUMN ' + QUOTENAME(@NewColumn) + N' uniqueidentifier NOT NULL;';
                EXEC sp_executesql @sql;
            END

            EXEC sp_executesql @create;
        END
        """;
}
