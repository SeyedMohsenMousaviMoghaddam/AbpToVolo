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
        var conversions = ZeroForeignKeys.FindConversions(root, IsZeroEntity, context.Options.IdType);
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
        FileSystem.WriteText(Path.Combine(efCore.Directory, DataScript), DataMigrationSql(renames, extensionProperties, legacyIds: context.Options.IdType == IdTypeMode.Source));

        context.Report.Change(StepName, $"Generated {DataScript} (users, roles, user roles, tenants, permission grants, settings)", context.Relative(efCore.CsprojPath));
        context.Report.Todo(StepName, "Database (on a copy first): 1) back it up and check it with squash-precheck.sql, 2) run the DbMigrator — it applies " +
                                      $"{RenameMigrationName}{(conversions.Count > 0 ? ", " + ZeroForeignKeys.MigrationName : "")} and {AbpIoModulesMigrationStep.MigrationName} " +
                                      $"(generated by AbpToVolo when the dotnet-ef tool is installed: ABP.io tables, {DataScript}, new foreign keys) and seeds the data, " +
                                      "3) check the copied users/roles/permissions (and dbo.Zero_UnmappedIds: audit ids of users that no longer exist), " +
                                      "4) drop the Zero_* tables.",
            context.Relative(efCore.CsprojPath));

        var lines = context.Report.Section("Module Zero");
        lines.Add($"Database: migration `{RenameMigrationName}` renames {renames.Count} Module Zero tables to `Zero_*` (constraint and index names too)" +
                  (conversions.Count > 0
                      ? $", migration `{ZeroForeignKeys.MigrationName}` converts {conversions.Count} user/tenant/role/audit columns of the solution's tables to Guid through `Zero_IdMap`"
                      : "") +
                  $", and `{DataScript}` copies users, roles, tenants, permissions and settings into the ABP.io tables with the same ids.");
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

    /// <summary>
    /// Saves the definition of every (non schema-bound) view in dbo.Zero_ViewDefinitions and drops the views: they
    /// reference Module Zero tables (renamed to Zero_*) and audit columns (CreatorUserId → CreatorId) and would stop the
    /// column changes ("Could not use view ... because of binding errors").
    /// </summary>
    internal const string SaveViewsSql = """
        SET NOCOUNT ON;
        IF OBJECT_ID(N'dbo.Zero_ViewDefinitions') IS NULL
            CREATE TABLE dbo.Zero_ViewDefinitions (SchemaName sysname NOT NULL, ViewName sysname NOT NULL, Definition nvarchar(max) NOT NULL,
                Recreated bit NOT NULL DEFAULT 0, Error nvarchar(4000) NULL, PRIMARY KEY (SchemaName, ViewName));

        INSERT INTO dbo.Zero_ViewDefinitions (SchemaName, ViewName, Definition)
        SELECT SCHEMA_NAME(v.schema_id), v.name, m.definition
        FROM sys.views v JOIN sys.sql_modules m ON m.object_id = v.object_id
        WHERE v.is_ms_shipped = 0 AND m.is_schema_bound = 0 AND m.definition IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM dbo.Zero_ViewDefinitions d WHERE d.SchemaName = SCHEMA_NAME(v.schema_id) AND d.ViewName = v.name);

        DECLARE @drop nvarchar(max) = N'';
        SELECT @drop += N'DROP VIEW ' + QUOTENAME(SchemaName) + N'.' + QUOTENAME(ViewName) + N';' + NCHAR(10)
        FROM dbo.Zero_ViewDefinitions
        WHERE Recreated = 0 AND OBJECT_ID(QUOTENAME(SchemaName) + N'.' + QUOTENAME(ViewName), N'V') IS NOT NULL;
        EXEC sp_executesql @drop;
        """;

    /// <summary>
    /// Recreates the saved views with the ABP.io audit column names; views that use other views are retried until no
    /// more can be created. A view that fails as it is is retried rewritten by Zero_ViewCompatSql: AbpSettings read
    /// through dbo.Zero_BoilerplateSettings (TenantId/UserId columns), int tenant literals (TenantId = 1) mapped with
    /// Zero_IdMap, then source user ids joined on AbpUsers.LegacyId. A view that still fails
    /// (e.g. it uses a Boilerplate column that has no ABP.io equivalent)
    /// does not stop the migration: its error is kept in dbo.Zero_ViewDefinitions (Recreated = 0) to be fixed by hand.
    /// </summary>
    internal const string ViewCompatFunctionSql = """
        CREATE OR ALTER FUNCTION dbo.Zero_ViewCompatSql(@sql nvarchar(max), @mode int)
        RETURNS nvarchar(max)
        AS
        BEGIN
            -- AbpToVolo: rewrites a Boilerplate view definition for the ABP.io tables; Zero_RecreateViews tries it only
            -- after the definition itself failed.
            DECLARE @at int, @end int, @pos int, @hit int, @alias sysname, @id uniqueidentifier, @variant int, @column nvarchar(40);
            IF @mode = 0
            BEGIN
                -- AbpSettings (TenantId/UserId columns) → the ABP.io settings with those columns
                SET @sql = REPLACE(REPLACE(REPLACE(@sql, N'[dbo].[AbpSettings]', N'dbo.Zero_BoilerplateSettings'),
                    N'dbo.AbpSettings', N'dbo.Zero_BoilerplateSettings'), N' AbpSettings', N' dbo.Zero_BoilerplateSettings');
                -- TenantId = 1 (an int tenant id) → the Guid id of that tenant, as a literal (no Zero_IdMap at run time)
                SET @sql = REPLACE(@sql, N'TenantId=', N'TenantId = ');
                SET @at = PATINDEX(N'%TenantId = [0-9]%', @sql);
                WHILE @at > 0
                BEGIN
                    SET @at += 11;
                    SET @end = PATINDEX(N'%[^0-9]%', SUBSTRING(@sql, @at, 30) + N' ');
                    SET @id = (SELECT NewId FROM dbo.Zero_IdMap WHERE EntityType = N'Tenant' AND OldId = CAST(SUBSTRING(@sql, @at, @end - 1) AS bigint));
                    SET @sql = STUFF(@sql, @at, @end - 1, CASE WHEN @id IS NULL THEN N'NULL' ELSE N'''' + CAST(@id AS nvarchar(36)) + N'''' END);
                    SET @at = PATINDEX(N'%TenantId = [0-9]%', @sql);
                END
                -- 1 AS CreatorUserId (an int user id, renamed CreatorId) → the Guid id of that user, as a literal
                SET @variant = 0;
                WHILE @variant < 3
                BEGIN
                    SET @column = CASE @variant WHEN 0 THEN N' AS CreatorId' WHEN 1 THEN N' AS LastModifierId' ELSE N' AS DeleterId' END;
                    SET @at = CHARINDEX(@column, @sql);
                    WHILE @at > 0
                    BEGIN
                        SET @end = @at;
                        WHILE @end > 1 AND SUBSTRING(@sql, @end - 1, 1) LIKE N'[0-9]' SET @end -= 1;
                        IF @end < @at AND (@end = 1 OR SUBSTRING(@sql, @end - 1, 1) NOT LIKE N'[A-Za-z0-9_.@]')
                        BEGIN
                            SET @id = (SELECT NewId FROM dbo.Zero_IdMap WHERE EntityType = N'User' AND OldId = CAST(SUBSTRING(@sql, @end, @at - @end) AS bigint));
                            SET @sql = STUFF(@sql, @end, @at - @end, CASE WHEN @id IS NULL THEN N'CAST(NULL AS uniqueidentifier)' ELSE N'CAST(''' + CAST(@id AS nvarchar(36)) + N''' AS uniqueidentifier)' END);
                            SET @at = CHARINDEX(@column, @sql, @end + 1);
                        END
                        ELSE SET @at = CHARINDEX(@column, @sql, @at + 1);
                    END
                    SET @variant += 1;
                END
                -- AbpTenants t ... t.Id = @TenantId (an int tenant id) → t.LegacyId when the tenants keep their source id
                SET @pos = 1;
                WHILE COL_LENGTH(N'dbo.AbpTenants', N'LegacyId') IS NOT NULL
                BEGIN
                    SET @hit = PATINDEX(N'%AbpTenants [A-Za-z_]%', SUBSTRING(@sql, @pos, LEN(@sql)));
                    IF @hit = 0 BREAK;
                    SET @at = @pos + @hit + 10;
                    IF SUBSTRING(@sql, @at, 3) = N'AS ' SET @at += 3;
                    SET @end = PATINDEX(N'%[^A-Za-z0-9_]%', SUBSTRING(@sql, @at, 200) + N' ');
                    SET @alias = SUBSTRING(@sql, @at, @end - 1);
                    IF @alias NOT IN (N'ON', N'WITH', N'WHERE', N'JOIN', N'LEFT', N'INNER', N'GROUP', N'ORDER')
                        SET @sql = REPLACE(REPLACE(@sql, @alias + N'.Id = @TenantId', @alias + N'.LegacyId = @TenantId'),
                            @alias + N'.Id=@TenantId', @alias + N'.LegacyId=@TenantId');
                    SET @pos = @at + 1;
                END
            END
            ELSE
            BEGIN
                -- x.SomeUserId = u.Id with SomeUserId still holding the source id → u.LegacyId
                SET @pos = 1;
                WHILE 1 = 1
                BEGIN
                    SET @hit = PATINDEX(N'%AbpUsers [A-Za-z_]%', SUBSTRING(@sql, @pos, LEN(@sql)));
                    IF @hit = 0 BREAK;
                    SET @at = @pos + @hit + 8;
                    IF SUBSTRING(@sql, @at, 3) = N'AS ' SET @at += 3;
                    SET @end = PATINDEX(N'%[^A-Za-z0-9_]%', SUBSTRING(@sql, @at, 200) + N' ');
                    SET @alias = SUBSTRING(@sql, @at, @end - 1);
                    IF @alias NOT IN (N'ON', N'WITH', N'WHERE', N'JOIN', N'LEFT', N'INNER', N'GROUP', N'ORDER')
                        SET @sql = REPLACE(REPLACE(@sql, N'UserId = ' + @alias + N'.Id', N'UserId = ' + @alias + N'.LegacyId'),
                            N'UserId=' + @alias + N'.Id', N'UserId=' + @alias + N'.LegacyId');
                    SET @pos = @at + 1;
                END
            END
            RETURN @sql;
        END
        """;

    /// <summary>
    /// Stored procedures and functions written against the Boilerplate schema: each is created again (CREATE OR ALTER)
    /// with the ABP.io audit column names, and when it takes the tenant as an int (@TenantId int) "TenantId = @TenantId"
    /// compares the Guid TenantId columns with the Guid of that tenant (the parameter, and so the application's calls,
    /// keep the int; otherwise "Operand type clash: uniqueidentifier is incompatible with int" when it runs). One that
    /// still fails is retried rewritten by Zero_ViewCompatSql like the views; one that never compiles is kept as it was
    /// and listed.
    /// </summary>
    internal const string ConvertModulesProcedureSql = """
        CREATE OR ALTER PROCEDURE dbo.Zero_ConvertModules
        AS
        BEGIN
            SET NOCOUNT ON;
            SET XACT_ABORT OFF;
            DECLARE @name nvarchar(300), @param sysname, @sql nvarchar(max), @base nvarchar(max), @new nvarchar(max),
                @pattern nvarchar(300), @expr nvarchar(400), @variant int, @at int, @word nvarchar(20), @attempt int,
                @done bit, @error nvarchar(4000), @failed nvarchar(max) = N'';
            DECLARE modules CURSOR LOCAL FAST_FORWARD FOR
                SELECT QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name), p.name, m.definition
                FROM sys.objects o
                JOIN sys.sql_modules m ON m.object_id = o.object_id
                LEFT JOIN sys.parameters p ON p.object_id = o.object_id AND p.name = N'@TenantId' AND TYPE_NAME(p.user_type_id) IN (N'int', N'bigint')
                WHERE o.is_ms_shipped = 0 AND o.type IN ('P', 'FN', 'IF', 'TF') AND o.name NOT LIKE N'Zero[_]%' AND m.is_schema_bound = 0;
            OPEN modules;
            FETCH NEXT FROM modules INTO @name, @param, @sql;
            WHILE @@FETCH_STATUS = 0
            BEGIN
                SET @base = REPLACE(REPLACE(REPLACE(@sql, N'CreatorUserId', N'CreatorId'), N'LastModifierUserId', N'LastModifierId'), N'DeleterUserId', N'DeleterId');
                IF @param IS NOT NULL
                BEGIN
                    SET @expr = CASE WHEN COL_LENGTH(N'dbo.AbpTenants', N'LegacyId') IS NOT NULL
                        THEN N'(SELECT zt.Id FROM dbo.AbpTenants zt WHERE zt.LegacyId = ' + @param + N')'
                        ELSE N'(SELECT zm.NewId FROM dbo.Zero_IdMap zm WHERE zm.EntityType = N''Tenant'' AND zm.OldId = ' + @param + N')' END;
                    SET @variant = 0;
                    WHILE @variant < 2
                    BEGIN
                        SET @pattern = CASE @variant WHEN 0 THEN N'TenantId = ' ELSE N'TenantId=' END + @param;
                        SET @at = CHARINDEX(@pattern, @base);
                        WHILE @at > 0
                        BEGIN
                            -- the column side only (not "@TenantId = @TenantId"), the whole parameter name (not @TenantIds)
                            IF (@at = 1 OR SUBSTRING(@base, @at - 1, 1) <> N'@') AND SUBSTRING(@base, @at + LEN(@pattern), 1) NOT LIKE N'[A-Za-z0-9_]'
                            BEGIN
                                SET @base = STUFF(@base, @at + LEN(@pattern) - LEN(@param), LEN(@param), @expr);
                                SET @at = CHARINDEX(@pattern, @base, @at + LEN(@pattern) - LEN(@param) + LEN(@expr));
                            END
                            ELSE SET @at = CHARINDEX(@pattern, @base, @at + 1);
                        END
                        SET @variant += 1;
                    END
                END

                -- CREATE → CREATE OR ALTER: the CREATE followed by PROC/FUNCTION (not "-- Create date:" of a template header)
                SET @at = CHARINDEX(N'CREATE', @base);
                WHILE @at > 0
                BEGIN
                    SET @word = LEFT(LTRIM(REPLACE(REPLACE(REPLACE(SUBSTRING(@base, @at + 6, 40), NCHAR(9), N' '), NCHAR(13), N' '), NCHAR(10), N' ')), 8);
                    IF @word LIKE N'PROC%' OR @word LIKE N'FUNCTION' OR @word LIKE N'OR ALTER' BREAK;
                    SET @at = CHARINDEX(N'CREATE', @base, @at + 6);
                END
                IF @at > 0 AND @word NOT LIKE N'OR ALTER' SET @base = STUFF(@base, @at, 6, N'CREATE OR ALTER');

                SELECT @attempt = 0, @done = 0, @error = NULL;
                WHILE @at > 0 AND @done = 0 AND @attempt <= 2
                BEGIN
                    SET @new = @base;
                    IF @attempt >= 1 SET @new = dbo.Zero_ViewCompatSql(@new, 0);
                    IF @attempt >= 2 SET @new = dbo.Zero_ViewCompatSql(@new, 1);
                    BEGIN TRY
                        EXEC sp_executesql @new;
                        SET @done = 1;
                    END TRY
                    BEGIN CATCH
                        IF @attempt = 0 SET @error = ERROR_MESSAGE();
                        SET @attempt += 1;
                    END CATCH
                END
                IF @done = 0 SET @failed += @name + N': ' + ISNULL(@error, N'CREATE not found') + NCHAR(10);
                FETCH NEXT FROM modules INTO @name, @param, @sql;
            END
            CLOSE modules;
            DEALLOCATE modules;
            IF @failed <> N'' PRINT N'AbpToVolo: procedures/functions to fix by hand (kept as they were):' + NCHAR(10) + @failed;
        END
        """;

    internal const string RecreateViewsProcedureSql = """
        CREATE OR ALTER PROCEDURE dbo.Zero_RecreateViews
        AS
        BEGIN
            SET NOCOUNT ON;
            SET XACT_ABORT OFF;
            DECLARE @progress bit = 1, @schema sysname, @name sysname, @definition nvarchar(max), @sql nvarchar(max),
                @attempt int, @done bit, @error nvarchar(4000);
            IF COL_LENGTH(N'dbo.AbpSettings', N'ProviderName') IS NOT NULL
                EXEC sp_executesql N'CREATE OR ALTER VIEW dbo.Zero_BoilerplateSettings
            AS
            -- AbpToVolo: the ABP.io settings with the Boilerplate AbpSettings columns, for views written against them.
            SELECT s.Id, s.Name, s.Value,
                CASE s.ProviderName WHEN N''T'' THEN TRY_CAST(s.ProviderKey AS uniqueidentifier) WHEN N''U'' THEN u.TenantId END AS TenantId,
                u.Id AS UserId
            FROM dbo.AbpSettings s
            LEFT JOIN dbo.AbpUsers u ON s.ProviderName = N''U'' AND u.Id = TRY_CAST(s.ProviderKey AS uniqueidentifier)
            WHERE s.ProviderName IN (N''G'', N''T'', N''U'')';
            WHILE @progress = 1
            BEGIN
                SET @progress = 0;
                DECLARE views CURSOR LOCAL FAST_FORWARD FOR
                    SELECT SchemaName, ViewName, Definition FROM dbo.Zero_ViewDefinitions WHERE Recreated = 0;
                OPEN views;
                FETCH NEXT FROM views INTO @schema, @name, @definition;
                WHILE @@FETCH_STATUS = 0
                BEGIN
                    SET @definition = REPLACE(REPLACE(REPLACE(@definition, N'CreatorUserId', N'CreatorId'), N'LastModifierUserId', N'LastModifierId'), N'DeleterUserId', N'DeleterId');
                    SELECT @attempt = 0, @done = 0, @error = NULL;
                    WHILE @done = 0 AND @attempt <= 2
                    BEGIN
                        SET @sql = @definition;
                        IF @attempt >= 1 SET @sql = dbo.Zero_ViewCompatSql(@sql, 0);
                        IF @attempt >= 2 SET @sql = dbo.Zero_ViewCompatSql(@sql, 1);
                        BEGIN TRY
                            IF OBJECT_ID(QUOTENAME(@schema) + N'.' + QUOTENAME(@name), N'V') IS NULL EXEC sp_executesql @sql;
                            UPDATE dbo.Zero_ViewDefinitions SET Recreated = 1, Error = NULL WHERE SchemaName = @schema AND ViewName = @name;
                            SELECT @progress = 1, @done = 1;
                        END TRY
                        BEGIN CATCH
                            IF @attempt = 0 SET @error = ERROR_MESSAGE();
                            SET @attempt += 1;
                        END CATCH
                    END
                    IF @done = 0 UPDATE dbo.Zero_ViewDefinitions SET Error = @error WHERE SchemaName = @schema AND ViewName = @name;
                    FETCH NEXT FROM views INTO @schema, @name, @definition;
                END
                CLOSE views;
                DEALLOCATE views;
            END

            DECLARE @failed nvarchar(max) = N'';
            SELECT @failed += QUOTENAME(SchemaName) + N'.' + QUOTENAME(ViewName) + N': ' + ISNULL(Error, N'?') + NCHAR(10)
            FROM dbo.Zero_ViewDefinitions WHERE Recreated = 0;
            IF @failed <> N'' PRINT N'AbpToVolo: views to fix by hand (see dbo.Zero_ViewDefinitions):' + NCHAR(10) + @failed;
        END
        """;

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
                        // Views reference the Module Zero tables and the audit columns that the next migrations change: their
                        // definitions are saved and they are dropped; AbpIoModules recreates them (EXEC dbo.Zero_RecreateViews).
                        migrationBuilder.Sql(@"{{SaveViewsSql.Replace("\"", "\"\"")}}");

                        migrationBuilder.Sql(@"{{ViewCompatFunctionSql.Replace("\"", "\"\"")}}");
                        migrationBuilder.Sql(@"{{RecreateViewsProcedureSql.Replace("\"", "\"\"")}}");
                        migrationBuilder.Sql(@"{{ConvertModulesProcedureSql.Replace("\"", "\"\"")}}");

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

    internal static string DataMigrationSql(Dictionary<string, string> renames, List<(ZeroKind Kind, string Property)> extensionProperties, bool legacyIds = false)
    {
        // IdTypeMode.Source: the old ids are kept as LegacyId (the application's columns reference them); the sequences of
        // new users/roles continue after the largest one.
        var legacyColumn = legacyIds ? ", [LegacyId]" : "";
        string LegacyValue(string alias) => legacyIds ? $", {alias}.Id" : "";
        var restartSequences = legacyIds
            ? """

                -- ---------- Legacy id sequences (new users, roles and tenants get the next numbers) ----------
                DECLARE @restartLegacyIds nvarchar(600) =
                    N'ALTER SEQUENCE dbo.AbpUserLegacyIds RESTART WITH ' + CAST(ISNULL((SELECT MAX(LegacyId) FROM dbo.[AbpUsers]), 0) + 1 AS nvarchar(30)) + N'; ' +
                    N'ALTER SEQUENCE dbo.AbpRoleLegacyIds RESTART WITH ' + CAST(ISNULL((SELECT MAX(LegacyId) FROM dbo.[AbpRoles]), 0) + 1 AS nvarchar(30)) + N';';
                IF OBJECT_ID(N'dbo.AbpTenantLegacyIds', N'SO') IS NOT NULL
                    SET @restartLegacyIds = @restartLegacyIds +
                        N' ALTER SEQUENCE dbo.AbpTenantLegacyIds RESTART WITH ' + CAST(ISNULL((SELECT MAX(LegacyId) FROM dbo.[AbpTenants]), 0) + 1 AS nvarchar(30)) + N';';
                EXEC sp_executesql @restartLegacyIds;
                """
            : "";
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

            INSERT INTO dbo.[AbpTenants] (Id, Name, NormalizedName, EntityVersion, IsDeleted, DeletionTime, LastModificationTime, CreationTime, ExtraProperties, ConcurrencyStamp{Columns(tenantExtensions)}{legacyColumn})
            SELECT m.NewId, n.Name, UPPER(n.Name), 0, t.IsDeleted, t.DeletionTime, t.LastModificationTime, t.CreationTime, N'{EmptyJson}', LOWER(REPLACE(CONVERT(nvarchar(36), NEWID()), N'-', N'')){Values("t", tenantExtensions)}{LegacyValue("t")}
            FROM dbo.[{T("AbpTenants")}] t
            -- Name = the Boilerplate display name (ABP.io: at most 64 characters, unique); TenancyName is kept as an extension.
            CROSS APPLY (SELECT LEFT(COALESCE(NULLIF(LTRIM(RTRIM(t.Name)), N''), t.TenancyName), 64) AS DisplayName) d
            CROSS APPLY (SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.[{T("AbpTenants")}] o WHERE o.Id <> t.Id AND
                LEFT(COALESCE(NULLIF(LTRIM(RTRIM(o.Name)), N''), o.TenancyName), 64) = d.DisplayName) THEN LEFT(t.TenancyName, 64) ELSE d.DisplayName END AS Name) n
            JOIN dbo.Zero_IdMap m ON m.EntityType = N'Tenant' AND m.OldId = t.Id
            WHERE NOT EXISTS (SELECT 1 FROM dbo.[AbpTenants] x WHERE x.Id = m.NewId);

            -- ---------- Users ----------
            INSERT INTO dbo.Zero_IdMap (EntityType, OldId, NewId)
            SELECT N'User', u.Id, NEWID() FROM dbo.[{T("AbpUsers")}] u
            WHERE NOT EXISTS (SELECT 1 FROM dbo.Zero_IdMap m WHERE m.EntityType = N'User' AND m.OldId = u.Id);

            INSERT INTO dbo.[AbpUsers] (Id, TenantId, UserName, NormalizedUserName, Name, Surname, Email, NormalizedEmail, EmailConfirmed, PasswordHash,
                SecurityStamp, IsExternal, PhoneNumber, PhoneNumberConfirmed, IsActive, TwoFactorEnabled, LockoutEnd, LockoutEnabled, AccessFailedCount,
                ShouldChangePasswordOnNextLogin, EntityVersion, Leaved, IsDeleted, DeletionTime, LastModificationTime, CreationTime, ExtraProperties, ConcurrencyStamp{Columns(userExtensions)}{legacyColumn})
            SELECT m.NewId, tm.NewId, u.UserName, u.NormalizedUserName, u.Name, u.Surname, u.EmailAddress, u.NormalizedEmailAddress, u.IsEmailConfirmed, u.Password,
                ISNULL(u.SecurityStamp, REPLACE(CONVERT(nvarchar(36), NEWID()), N'-', N'')), 0, u.PhoneNumber, u.IsPhoneNumberConfirmed, u.IsActive, u.IsTwoFactorEnabled,
                u.LockoutEndDateUtc, u.IsLockoutEnabled, u.AccessFailedCount, 0, 0, 0, u.IsDeleted, u.DeletionTime, u.LastModificationTime, u.CreationTime, N'{EmptyJson}',
                LOWER(REPLACE(CONVERT(nvarchar(36), NEWID()), N'-', N'')){Values("u", userExtensions)}{LegacyValue("u")}
            FROM dbo.[{T("AbpUsers")}] u
            JOIN dbo.Zero_IdMap m ON m.EntityType = N'User' AND m.OldId = u.Id
            LEFT JOIN dbo.Zero_IdMap tm ON tm.EntityType = N'Tenant' AND tm.OldId = u.TenantId
            WHERE NOT EXISTS (SELECT 1 FROM dbo.[AbpUsers] x WHERE x.Id = m.NewId);

            -- ---------- Roles ----------
            INSERT INTO dbo.Zero_IdMap (EntityType, OldId, NewId)
            SELECT N'Role', r.Id, NEWID() FROM dbo.[{T("AbpRoles")}] r
            WHERE NOT EXISTS (SELECT 1 FROM dbo.Zero_IdMap m WHERE m.EntityType = N'Role' AND m.OldId = r.Id);

            INSERT INTO dbo.[AbpRoles] (Id, TenantId, Name, NormalizedName, IsDefault, IsStatic, IsPublic, EntityVersion, CreationTime, ExtraProperties, ConcurrencyStamp{Columns(roleExtensions)}{legacyColumn})
            SELECT m.NewId, tm.NewId, r.Name, r.NormalizedName, r.IsDefault, r.IsStatic, 1, 0, r.CreationTime, N'{EmptyJson}', LOWER(REPLACE(CONVERT(nvarchar(36), NEWID()), N'-', N'')){Values("r", roleExtensions)}{LegacyValue("r")}
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
              AND NOT EXISTS (SELECT 1 FROM dbo.[AbpPermissionGrants] x WHERE x.Name = p.Name AND x.ProviderName = N'R' AND x.ProviderKey = r.Name
                              AND (x.TenantId = tm.NewId OR (x.TenantId IS NULL AND tm.NewId IS NULL)));

            INSERT INTO dbo.[AbpPermissionGrants] (Id, TenantId, Name, ProviderName, ProviderKey)
            SELECT NEWID(), tm.NewId, p.Name, N'U', CONVERT(nvarchar(64), um.NewId)
            FROM dbo.[{T("AbpPermissions")}] p
            JOIN dbo.Zero_IdMap um ON um.EntityType = N'User' AND um.OldId = p.UserId
            LEFT JOIN dbo.Zero_IdMap tm ON tm.EntityType = N'Tenant' AND tm.OldId = p.TenantId
            WHERE p.IsGranted = 1 AND p.UserId IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM dbo.[AbpPermissionGrants] x WHERE x.Name = p.Name AND x.ProviderName = N'U' AND x.ProviderKey = CONVERT(nvarchar(64), um.NewId));

            -- ---------- Settings (user → "U" + user id, tenant → "T" + tenant id, application → "G"; the latest of duplicates) ----------
            INSERT INTO dbo.[AbpSettings] (Id, Name, Value, ProviderName, ProviderKey)
            SELECT NEWID(), d.Name, d.Value, d.ProviderName, d.ProviderKey
            FROM (
                SELECT p.Name, p.Value, p.ProviderName, p.ProviderKey,
                       ROW_NUMBER() OVER (PARTITION BY p.Name, p.ProviderName, p.ProviderKey ORDER BY p.Id DESC) AS RowNumber
                FROM (
                    SELECT s.Id, s.Name, ISNULL(s.Value, N'') AS Value,
                           CASE WHEN s.UserId IS NOT NULL THEN N'U' WHEN s.TenantId IS NOT NULL THEN N'T' ELSE N'G' END AS ProviderName,
                           CASE WHEN s.UserId IS NOT NULL THEN LOWER(CONVERT(nvarchar(64), um.NewId))
                                WHEN s.TenantId IS NOT NULL THEN LOWER(CONVERT(nvarchar(64), tm.NewId)) END AS ProviderKey
                    FROM dbo.[{T("AbpSettings")}] s
                    LEFT JOIN dbo.Zero_IdMap um ON um.EntityType = N'User' AND um.OldId = s.UserId
                    LEFT JOIN dbo.Zero_IdMap tm ON tm.EntityType = N'Tenant' AND tm.OldId = s.TenantId
                    WHERE (s.UserId IS NULL OR um.NewId IS NOT NULL) AND (s.TenantId IS NULL OR s.UserId IS NOT NULL OR tm.NewId IS NOT NULL)
                ) p
            ) d
            WHERE d.RowNumber = 1
              AND NOT EXISTS (SELECT 1 FROM dbo.[AbpSettings] x
                              WHERE x.Name = d.Name AND x.ProviderName = d.ProviderName AND ISNULL(x.ProviderKey, N'') = ISNULL(d.ProviderKey, N''));
            {restartSequences}

            COMMIT TRANSACTION;
            PRINT N'Module Zero data copied. Convert user/tenant foreign keys of your own tables with dbo.Zero_IdMap, then drop the Zero_* tables.';

            """;
    }
}
