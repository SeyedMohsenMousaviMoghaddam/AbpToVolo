using System.Text;
using AbpToVolo.Core;
using AbpToVolo.Mappings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AbpToVolo.Steps;

/// <summary>
/// Database side of the Module Zero replacement (SQL Server).
/// Module Zero's tables (AbpUsers, AbpRoles, AbpTenants, AbpSettings...) have the same names as the ABP.io module tables
/// but a different schema (bigint/int ids → uniqueidentifier). So:
///  1. a generated migration (RenameModuleZeroTables) renames them, with their PK/FK/index names, to Zero_*,
///     and the model snapshot is updated accordingly — ABP.io can then create its own tables;
///  2. ModuleZeroDataMigration.sql copies users, roles, user roles, tenants and permission grants into the ABP.io tables
///     (password hashes are compatible: both use ASP.NET Core Identity), keeping an old→new id map (Zero_IdMap).
/// </summary>
internal static class ZeroDatabase
{
    private const string StepName = "Module Zero → ABP.io modules";
    public const string RenameMigrationName = "RenameModuleZeroTables";
    public const string DataScript = "ModuleZeroDataMigration.sql";
    private const string EmptyJson = "{}";

    public static void Prepare(MigrationContext context, List<string> zeroEntityNames, List<(ZeroKind Kind, string Property)> extensionProperties)
    {
        var efCore = context.FindProject(ProjectLayer.EntityFrameworkCore);
        var snapshot = efCore is null ? null : FileSystem.EnumerateFiles(efCore.Directory, "*ModelSnapshot.cs").FirstOrDefault();
        if (efCore is null || snapshot is null)
        {
            context.Report.Todo(StepName, "No EF Core model snapshot found: create the ABP.io tables with a new migration and copy the Module Zero data manually.");
            return;
        }

        var folder = Path.GetDirectoryName(snapshot)!;
        var source = File.ReadAllText(snapshot);
        var (updatedSnapshot, renames) = RenameTablesInSnapshot(source, zeroEntityNames);
        if (renames.Count == 0)
        {
            context.Report.Info(StepName, "The model snapshot contains no Module Zero tables.");
            return;
        }

        var root = CSharpSyntaxTree.ParseText(updatedSnapshot).GetCompilationUnitRoot();
        var dbContext = root.DescendantNodes().OfType<AttributeSyntax>().First(a => a.Name.ToString() == "DbContext").ArgumentList!.Arguments[0].ToString();
        var ns = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().First().Name.ToString();
        var usings = string.Concat(root.Usings.Select(u => u.ToFullString()));

        var id = MigrationId(context, folder, RenameMigrationName);
        WriteDesigner(folder, id, RenameMigrationName, updatedSnapshot, ns, dbContext, usings);
        FileSystem.WriteText(Path.Combine(folder, $"{id}_{RenameMigrationName}.cs"), RenameMigration(ns, renames));
        context.Report.Change(StepName, $"Generated migration {id}_{RenameMigrationName}: {renames.Count} Module Zero tables → Zero_* (snapshot updated)", context.Relative(folder));

        // The solution's own tables: user/tenant/role foreign keys and audit columns → Guid (ABP.io model), keeping the data.
        bool IsZeroEntity(string name) => name.StartsWith("Abp.", StringComparison.Ordinal) || zeroEntityNames.Contains(name);
        string ZeroTable(string table) => renames.TryGetValue(table, out var renamed) ? renamed : "Zero_" + table;
        var conversions = ZeroForeignKeys.FindConversions(root, IsZeroEntity);
        var finalSnapshot = updatedSnapshot;
        if (conversions.Count > 0)
        {
            finalSnapshot = ZeroForeignKeys.UpdateSnapshot(root, conversions, IsZeroEntity);
            var keysId = MigrationId(context, folder, ZeroForeignKeys.MigrationName);
            WriteDesigner(folder, keysId, ZeroForeignKeys.MigrationName, finalSnapshot, ns, dbContext, usings);
            FileSystem.WriteText(Path.Combine(folder, $"{keysId}_{ZeroForeignKeys.MigrationName}.cs"), ZeroForeignKeys.Migration(ns, conversions, ZeroTable));

            var audit = conversions.Count(c => c.NewColumn != c.Column);
            context.Report.Change(StepName, $"Generated migration {keysId}_{ZeroForeignKeys.MigrationName}: {conversions.Count} columns in " +
                                            $"{conversions.Select(c => c.Table).Distinct().Count()} tables → uniqueidentifier through Zero_IdMap " +
                                            $"({audit} audit columns renamed: CreatorUserId → CreatorId ...), data kept", context.Relative(folder));
            if (conversions.Any(c => c.Kind == "OrganizationUnit"))
            {
                context.Report.Todo(StepName, $"{DataScript} does not copy organization units: copy AbpOrganizationUnits with the ids of Zero_IdMap " +
                                              "(EntityType = 'OrganizationUnit') before the foreign keys to them are added.", context.Relative(folder));
            }
        }

        FileSystem.WriteText(snapshot, finalSnapshot);
        FileSystem.WriteText(Path.Combine(efCore.Directory, DataScript), DataMigrationSql(renames, extensionProperties));

        context.Report.Change(StepName, $"Generated {DataScript} (users, roles, user roles, tenants, permission grants)", context.Relative(efCore.CsprojPath));
        context.Report.Todo(StepName, "Database (on a copy first): 1) dotnet ef database update (applies the squashed migration, " +
                                      $"{RenameMigrationName}{(conversions.Count > 0 ? " and " + ZeroForeignKeys.MigrationName : "")}), " +
                                      "2) dotnet ef migrations add AbpIoModules (creates the ABP.io tables and the new foreign keys; remove its DropTable(\"Zero_*\") " +
                                      $"calls until the data is copied), 3) run {DataScript} before applying AbpIoModules' foreign keys — simplest: put its content in " +
                                      "AbpIoModules with migrationBuilder.Sql(...) after the CreateTable calls, 4) dotnet ef database update, 5) drop the Zero_* tables.",
            context.Relative(efCore.CsprojPath));

        var lines = context.Report.Section("Module Zero");
        lines.Add($"Database: migration `{RenameMigrationName}` renames {renames.Count} Module Zero tables to `Zero_*` (constraint and index names too)" +
                  (conversions.Count > 0
                      ? $", migration `{ZeroForeignKeys.MigrationName}` converts {conversions.Count} user/tenant/role/audit columns of the solution's tables to Guid through `Zero_IdMap`"
                      : "") +
                  $", and `{DataScript}` copies users, roles, tenants and permissions into the ABP.io tables with the same ids.");
    }

    private static void WriteDesigner(string folder, string id, string name, string snapshotSource, string ns, string dbContext, string usings)
    {
        var root = CSharpSyntaxTree.ParseText(snapshotSource).GetCompilationUnitRoot();
        var buildModel = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First(m => m.Identifier.Text == "BuildModel");
        var nl = Environment.NewLine;
        var body = string.Concat(buildModel.Body!.Statements.Select(s => s.ToFullString())) + buildModel.Body.CloseBraceToken.LeadingTrivia.ToFullString();

        FileSystem.WriteText(Path.Combine(folder, $"{id}_{name}.Designer.cs"),
            $"// <auto-generated />{nl}{usings}using Microsoft.EntityFrameworkCore.Migrations;{nl}{nl}#nullable disable{nl}{nl}namespace {ns}{nl}{{{nl}" +
            $"    [DbContext({dbContext})]{nl}    [Migration(\"{id}_{name}\")]{nl}    partial class {name}{nl}    {{{nl}" +
            $"        protected override void BuildTargetModel(ModelBuilder modelBuilder){nl}        {{{nl}{body.TrimEnd()}{nl}        }}{nl}    }}{nl}}}{nl}");
    }

    /// <summary>In every modelBuilder.Entity("Abp.*" / Zero entity) block, b.ToTable("X") → b.ToTable("Zero_X").</summary>
    internal static (string Source, Dictionary<string, string> Renames) RenameTablesInSnapshot(string source, IReadOnlyCollection<string> zeroEntityNames)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        var edits = new Dictionary<SyntaxNode, SyntaxNode>();

        foreach (var entity in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (entity.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Entity" } ||
                entity.ArgumentList.Arguments.FirstOrDefault()?.Expression is not LiteralExpressionSyntax { Token.ValueText: var entityName } ||
                !(entityName.StartsWith("Abp.", StringComparison.Ordinal) || zeroEntityNames.Contains(entityName)))
            {
                continue;
            }

            foreach (var toTable in entity.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (toTable.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "ToTable" } &&
                    toTable.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax { Token.ValueText: var table } literal &&
                    !table.StartsWith("Zero_", StringComparison.Ordinal))
                {
                    renames[table] = "Zero_" + table;
                    edits[literal] = SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal("Zero_" + table)).WithTriviaFrom(literal);
                }
            }
        }

        return (root.ReplaceNodes(edits.Keys, (original, _) => edits[original]).ToFullString(), renames);
    }

    /// <summary>
    /// Id of a generated migration: the one of an earlier conversion (state.json), otherwise one second after the last
    /// existing migration — deterministic (not the current time), so re-converting gives the same files.
    /// </summary>
    private static string MigrationId(MigrationContext context, string folder, string name)
    {
        if (context.State.MigrationIds.TryGetValue(name, out var known)) return known;

        var existing = FileSystem.EnumerateFiles(folder, "*.cs")
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "\\[Migration\\(\"(\\d{14})_").Select(m => m.Groups[1].Value))
            .Concat(context.State.MigrationIds.Values)
            .DefaultIfEmpty("")
            .Max(StringComparer.Ordinal)!;
        var id = DateTime.TryParseExact(existing, "yyyyMMddHHmmss", null,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var last)
            ? last.AddSeconds(1)
            : DateTime.UtcNow;

        var result = id.ToString("yyyyMMddHHmmss");
        context.State.MigrationIds[name] = result;
        return result;
    }

    private static string RenameMigration(string ns, Dictionary<string, string> renames)
    {
        var up = RenameSql(renames);
        var down = RenameSql(renames.ToDictionary(r => r.Value, r => r.Key));
        return $$"""
            // <auto-generated>
            //   Generated by AbpToVolo. Renames the Module Zero tables (and their PK/FK/index/default constraint names)
            //   to Zero_*, so the ABP.io module tables (AbpUsers, AbpRoles, AbpTenants, ...) can be created next to them.
            //   SQL Server only. The data is copied later with ModuleZeroDataMigration.sql.
            // </auto-generated>
            using Microsoft.EntityFrameworkCore.Migrations;

            #nullable disable

            namespace {{ns}}
            {
                public partial class {{RenameMigrationName}} : Migration
                {
                    protected override void Up(MigrationBuilder migrationBuilder)
                    {
                        migrationBuilder.Sql(@"{{up.Replace("\"", "\"\"")}}");
                    }

                    protected override void Down(MigrationBuilder migrationBuilder)
                    {
                        migrationBuilder.Sql(@"{{down.Replace("\"", "\"\"")}}");
                    }
                }
            }

            """;
    }

    /// <summary>
    /// T-SQL that renames tables and every constraint/index whose name contains one of the table names
    /// (EF Core names them PK_Table, FK_Dependent_Principal_Column, IX_Table_Column).
    /// </summary>
    internal static string RenameSql(Dictionary<string, string> renames)
    {
        var sb = new StringBuilder();
        // Delimited replace: "_AbpUsers_" → "_Zero_AbpUsers_", longest names first.
        var padded = "N'_' + {0} + N'_'";
        foreach (var (oldName, newName) in renames.OrderByDescending(r => r.Key.Length))
        {
            padded = $"REPLACE({padded}, N'_{oldName}_', N'_{newName}_')";
        }

        string NewName(string column) => $"SUBSTRING({string.Format(padded, column)}, 2, LEN({string.Format(padded, column)}) - 2)";

        sb.AppendLine("SET NOCOUNT ON;");
        sb.AppendLine("DECLARE @map TABLE (OldName sysname PRIMARY KEY, NewName sysname NOT NULL);");
        sb.AppendLine("INSERT INTO @map (OldName, NewName) VALUES");
        sb.AppendLine(string.Join("," + Environment.NewLine, renames.Select(r => $"    (N'{r.Key}', N'{r.Value}')")) + ";");
        sb.AppendLine("DECLARE @sql nvarchar(max) = N'';");
        sb.AppendLine();
        sb.AppendLine("-- constraints of the renamed tables, and foreign keys that reference them");
        sb.AppendLine("SELECT @sql += N'EXEC sp_rename N''' + QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name) + N''', N''' + x.NewName + N''', N''OBJECT'';' + NCHAR(10)");
        sb.AppendLine("FROM sys.objects o");
        sb.AppendLine("JOIN sys.tables t ON t.object_id = o.parent_object_id");
        sb.AppendLine("LEFT JOIN sys.foreign_keys fk ON fk.object_id = o.object_id");
        sb.AppendLine("LEFT JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id");
        sb.AppendLine($"CROSS APPLY (SELECT {NewName("o.name")} AS NewName) x");
        sb.AppendLine("WHERE o.type IN ('PK', 'F', 'UQ', 'D', 'C')");
        sb.AppendLine("  AND (t.name IN (SELECT OldName FROM @map) OR rt.name IN (SELECT OldName FROM @map))");
        sb.AppendLine("  AND x.NewName <> o.name;");
        sb.AppendLine();
        sb.AppendLine("-- indexes of the renamed tables");
        sb.AppendLine("SELECT @sql += N'EXEC sp_rename N''' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name) + N'.' + QUOTENAME(i.name) + N''', N''' + x.NewName + N''', N''INDEX'';' + NCHAR(10)");
        sb.AppendLine("FROM sys.indexes i");
        sb.AppendLine("JOIN sys.tables t ON t.object_id = i.object_id");
        sb.AppendLine($"CROSS APPLY (SELECT {NewName("i.name")} AS NewName) x");
        sb.AppendLine("WHERE i.name IS NOT NULL AND i.is_primary_key = 0 AND i.is_unique_constraint = 0");
        sb.AppendLine("  AND t.name IN (SELECT OldName FROM @map) AND x.NewName <> i.name;");
        sb.AppendLine();
        sb.AppendLine("-- the tables");
        sb.AppendLine("SELECT @sql += N'EXEC sp_rename N''' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name) + N''', N''' + m.NewName + N''';' + NCHAR(10)");
        sb.AppendLine("FROM sys.tables t JOIN @map m ON m.OldName = t.name;");
        sb.AppendLine();
        sb.AppendLine("EXEC sp_executesql @sql;");
        return sb.ToString();
    }

    internal static string DataMigrationSql(Dictionary<string, string> renames, List<(ZeroKind Kind, string Property)> extensionProperties)
    {
        string T(string table) => renames.TryGetValue(table, out var renamed) ? renamed : "Zero_" + table;
        var userExtensions = extensionProperties.Where(p => p.Kind == ZeroKind.User).Select(p => p.Property).ToList();
        var roleExtensions = extensionProperties.Where(p => p.Kind == ZeroKind.Role).Select(p => p.Property).ToList();
        var tenantExtensions = extensionProperties.Where(p => p.Kind == ZeroKind.Tenant).Select(p => p.Property).ToList();
        string Columns(List<string> names) => string.Concat(names.Select(n => $", [{n}]"));
        string Values(string alias, List<string> names) => string.Concat(names.Select(n => $", {alias}.[{n}]"));

        return $"""
            -- =====================================================================================================
            -- Generated by AbpToVolo: copies Module Zero data into the ABP.io Identity / TenantManagement /
            -- PermissionManagement tables. REVIEW BEFORE RUNNING, on a copy of the database first.
            --
            -- Order: 1) migration {RenameMigrationName} has renamed the Zero tables to Zero_*
            --        2) migration {ZeroForeignKeys.MigrationName} (if generated) has filled dbo.Zero_IdMap and converted the
            --           user/tenant/role foreign keys and audit columns of the solution's tables to these Guid ids
            --        3) the ABP.io tables exist (dotnet ef migrations add AbpIoModules), before their foreign keys are enforced
            --        4) run this script
            -- Old ids are mapped to new Guid ids in dbo.Zero_IdMap; the rows below are copied with exactly these ids.
            -- Password hashes are copied as they are (ASP.NET Core Identity format, compatible).
            -- =====================================================================================================
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;

            IF OBJECT_ID(N'dbo.Zero_IdMap') IS NULL
                CREATE TABLE dbo.Zero_IdMap (EntityType nvarchar(32) NOT NULL, OldId bigint NOT NULL, NewId uniqueidentifier NOT NULL, PRIMARY KEY (EntityType, OldId));

            -- ---------- Tenants ----------
            INSERT INTO dbo.Zero_IdMap (EntityType, OldId, NewId)
            SELECT N'Tenant', t.Id, NEWID() FROM dbo.[{T("AbpTenants")}] t
            WHERE NOT EXISTS (SELECT 1 FROM dbo.Zero_IdMap m WHERE m.EntityType = N'Tenant' AND m.OldId = t.Id);

            INSERT INTO dbo.[AbpTenants] (Id, Name, NormalizedName, EntityVersion, IsDeleted, DeletionTime, LastModificationTime, CreationTime, ExtraProperties, ConcurrencyStamp{Columns(tenantExtensions)})
            SELECT m.NewId, t.TenancyName, UPPER(t.TenancyName), 0, t.IsDeleted, t.DeletionTime, t.LastModificationTime, t.CreationTime, N'{EmptyJson}', LOWER(REPLACE(CONVERT(nvarchar(36), NEWID()), N'-', N'')){Values("t", tenantExtensions)}
            FROM dbo.[{T("AbpTenants")}] t
            JOIN dbo.Zero_IdMap m ON m.EntityType = N'Tenant' AND m.OldId = t.Id
            WHERE NOT EXISTS (SELECT 1 FROM dbo.[AbpTenants] x WHERE x.Id = m.NewId);

            -- ---------- Users ----------
            INSERT INTO dbo.Zero_IdMap (EntityType, OldId, NewId)
            SELECT N'User', u.Id, NEWID() FROM dbo.[{T("AbpUsers")}] u
            WHERE NOT EXISTS (SELECT 1 FROM dbo.Zero_IdMap m WHERE m.EntityType = N'User' AND m.OldId = u.Id);

            INSERT INTO dbo.[AbpUsers] (Id, TenantId, UserName, NormalizedUserName, Name, Surname, Email, NormalizedEmail, EmailConfirmed, PasswordHash,
                SecurityStamp, IsExternal, PhoneNumber, PhoneNumberConfirmed, IsActive, TwoFactorEnabled, LockoutEnd, LockoutEnabled, AccessFailedCount,
                ShouldChangePasswordOnNextLogin, EntityVersion, Leaved, IsDeleted, DeletionTime, LastModificationTime, CreationTime, ExtraProperties, ConcurrencyStamp{Columns(userExtensions)})
            SELECT m.NewId, tm.NewId, u.UserName, u.NormalizedUserName, u.Name, u.Surname, u.EmailAddress, u.NormalizedEmailAddress, u.IsEmailConfirmed, u.Password,
                ISNULL(u.SecurityStamp, REPLACE(CONVERT(nvarchar(36), NEWID()), N'-', N'')), 0, u.PhoneNumber, u.IsPhoneNumberConfirmed, u.IsActive, u.IsTwoFactorEnabled,
                u.LockoutEndDateUtc, u.IsLockoutEnabled, u.AccessFailedCount, 0, 0, 0, u.IsDeleted, u.DeletionTime, u.LastModificationTime, u.CreationTime, N'{EmptyJson}',
                LOWER(REPLACE(CONVERT(nvarchar(36), NEWID()), N'-', N'')){Values("u", userExtensions)}
            FROM dbo.[{T("AbpUsers")}] u
            JOIN dbo.Zero_IdMap m ON m.EntityType = N'User' AND m.OldId = u.Id
            LEFT JOIN dbo.Zero_IdMap tm ON tm.EntityType = N'Tenant' AND tm.OldId = u.TenantId
            WHERE NOT EXISTS (SELECT 1 FROM dbo.[AbpUsers] x WHERE x.Id = m.NewId);

            -- ---------- Roles ----------
            INSERT INTO dbo.Zero_IdMap (EntityType, OldId, NewId)
            SELECT N'Role', r.Id, NEWID() FROM dbo.[{T("AbpRoles")}] r
            WHERE NOT EXISTS (SELECT 1 FROM dbo.Zero_IdMap m WHERE m.EntityType = N'Role' AND m.OldId = r.Id);

            INSERT INTO dbo.[AbpRoles] (Id, TenantId, Name, NormalizedName, IsDefault, IsStatic, IsPublic, EntityVersion, CreationTime, ExtraProperties, ConcurrencyStamp{Columns(roleExtensions)})
            SELECT m.NewId, tm.NewId, r.Name, r.NormalizedName, r.IsDefault, r.IsStatic, 1, 0, r.CreationTime, N'{EmptyJson}', LOWER(REPLACE(CONVERT(nvarchar(36), NEWID()), N'-', N'')){Values("r", roleExtensions)}
            FROM dbo.[{T("AbpRoles")}] r
            JOIN dbo.Zero_IdMap m ON m.EntityType = N'Role' AND m.OldId = r.Id
            LEFT JOIN dbo.Zero_IdMap tm ON tm.EntityType = N'Tenant' AND tm.OldId = r.TenantId
            WHERE r.IsDeleted = 0 AND NOT EXISTS (SELECT 1 FROM dbo.[AbpRoles] x WHERE x.Id = m.NewId);

            -- ---------- User roles ----------
            INSERT INTO dbo.[AbpUserRoles] (UserId, RoleId, TenantId)
            SELECT DISTINCT um.NewId, rm.NewId, tm.NewId
            FROM dbo.[{T("AbpUserRoles")}] ur
            JOIN dbo.Zero_IdMap um ON um.EntityType = N'User' AND um.OldId = ur.UserId
            JOIN dbo.Zero_IdMap rm ON rm.EntityType = N'Role' AND rm.OldId = ur.RoleId
            LEFT JOIN dbo.Zero_IdMap tm ON tm.EntityType = N'Tenant' AND tm.OldId = ur.TenantId
            WHERE EXISTS (SELECT 1 FROM dbo.[AbpRoles] r WHERE r.Id = rm.NewId)
              AND NOT EXISTS (SELECT 1 FROM dbo.[AbpUserRoles] x WHERE x.UserId = um.NewId AND x.RoleId = rm.NewId);

            -- ---------- Permission grants (only granted ones: ABP.io stores explicit grants) ----------
            INSERT INTO dbo.[AbpPermissionGrants] (Id, TenantId, Name, ProviderName, ProviderKey)
            SELECT NEWID(), tm.NewId, p.Name, N'R', r.Name
            FROM dbo.[{T("AbpPermissions")}] p
            JOIN dbo.[{T("AbpRoles")}] r ON r.Id = p.RoleId
            LEFT JOIN dbo.Zero_IdMap tm ON tm.EntityType = N'Tenant' AND tm.OldId = p.TenantId
            WHERE p.IsGranted = 1 AND p.RoleId IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM dbo.[AbpPermissionGrants] x WHERE x.Name = p.Name AND x.ProviderName = N'R' AND x.ProviderKey = r.Name);

            INSERT INTO dbo.[AbpPermissionGrants] (Id, TenantId, Name, ProviderName, ProviderKey)
            SELECT NEWID(), tm.NewId, p.Name, N'U', CONVERT(nvarchar(64), um.NewId)
            FROM dbo.[{T("AbpPermissions")}] p
            JOIN dbo.Zero_IdMap um ON um.EntityType = N'User' AND um.OldId = p.UserId
            LEFT JOIN dbo.Zero_IdMap tm ON tm.EntityType = N'Tenant' AND tm.OldId = p.TenantId
            WHERE p.IsGranted = 1 AND p.UserId IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM dbo.[AbpPermissionGrants] x WHERE x.Name = p.Name AND x.ProviderName = N'U' AND x.ProviderKey = CONVERT(nvarchar(64), um.NewId));

            COMMIT TRANSACTION;
            PRINT N'Module Zero data copied. Convert user/tenant foreign keys of your own tables with dbo.Zero_IdMap, then drop the Zero_* tables.';

            """;
    }
}
