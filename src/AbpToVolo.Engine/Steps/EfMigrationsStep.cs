using System.Text;
using AbpToVolo.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AbpToVolo.Steps;

/// <summary>
/// Handles the existing EF Core migrations according to <see cref="MigrationOptions.Migrations"/>.
///
/// Squash (default) merges every migration into a single one while keeping the history:
/// - the merged migration keeps the id of the LAST original migration, so databases that already applied it
///   (their __EFMigrationsHistory has that row) treat it as applied, and nothing in their history changes;
/// - a new database runs every original Up() in the original order (each one in its own block), so it ends up
///   with exactly the same schema, views and SQL objects as before;
/// - only the last designer (target model) is kept — the per-migration designers are removed;
/// - MIGRATION-HISTORY.md lists every merged migration and squash-precheck.sql verifies a database before deployment.
///
/// Migration folders are never rewritten by the C# step: column names come from anonymous member names
/// (e.g. CreatorUserId) and must stay exactly as they were.
/// </summary>
public sealed class EfMigrationsStep : IMigrationStep
{
    public const string SquashedClassName = "SquashedBoilerplateMigrations";
    public const string HistoryFile = "MIGRATION-HISTORY.md";
    public const string PrecheckFile = "squash-precheck.sql";

    private sealed record MigrationSource(
        string Id,
        string ClassName,
        string MainFile,
        string? DesignerFile,
        string UpBody,
        string DownBody,
        IReadOnlyList<string> Usings,
        IReadOnlyList<string> ExtraMembers);

    public string Name => "EF Core migrations";

    public void Execute(MigrationContext context)
    {
        foreach (var snapshot in FileSystem.EnumerateFiles(context.OutputRoot, "*ModelSnapshot.cs").ToList())
        {
            var folder = Path.GetDirectoryName(snapshot)!;
            switch (context.Options.Migrations)
            {
                case MigrationsMode.Delete:
                    Delete(context, folder);
                    break;
                case MigrationsMode.Keep:
                    context.ExcludedDirectories.Add(folder);
                    StripBoilerplateUsings(context, Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories));
                    context.Report.Info(Name, $"Migrations kept unchanged in {context.Relative(folder)}");
                    break;
                default:
                    context.ExcludedDirectories.Add(folder);
                    Squash(context, folder, snapshot);
                    break;
            }
        }
    }

    private void Delete(MigrationContext context, string folder)
    {
        var count = Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories).Count();
        Directory.Delete(folder, recursive: true);
        context.Report.Change(Name, $"Removed {count} old migration files in {context.Relative(folder)}");
        context.Report.Todo(Name, "Create a new initial migration for the ABP.io schema: dotnet ef migrations add Initial " +
                                  "(and migrate the existing data with SQL scripts if this is a live database).");
    }

    private void Squash(MigrationContext context, string folder, string snapshot)
    {
        var relativeFolder = context.Relative(folder);
        var (migrations, skipped) = LoadMigrations(folder, snapshot);

        foreach (var file in skipped)
        {
            context.Report.Warning(Name, "File in the migrations folder is not a migration with a [Migration] id; left as is.", context.Relative(file));
        }

        // Re-conversion of a newer source: the squash point of the first conversion never moves (deployed databases
        // know only that id). Later source migrations are not squashed and the snapshot stays at the squash point,
        // so the working branch creates the ABP.io equivalent with "dotnet ef migrations add" from the converted model.
        if (context.State.SquashedUpTo is { } upTo)
        {
            if (migrations.Any(m => m.Id == upTo))
            {
                var later = migrations.Where(m => string.CompareOrdinal(m.Id, upTo) > 0).ToList();
                migrations = migrations.Where(m => string.CompareOrdinal(m.Id, upTo) <= 0).ToList();
                if (later.Count > 0) ReportLaterMigrations(context, folder, snapshot, migrations[^1], later);
            }
            else
            {
                context.Report.Warning(Name, $"The squash point of the earlier conversion ({upTo}) is not among the source migrations; " +
                                             "the migrations are squashed again up to the last one.", relativeFolder);
            }
        }

        if (migrations.Count >= 2) context.State.SquashedUpTo ??= migrations[^1].Id;

        if (migrations.Count < 2)
        {
            StripBoilerplateUsings(context, Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories));
            context.Report.Info(Name, $"{migrations.Count} migration(s) in {relativeFolder}: nothing to squash.");
            return;
        }

        var last = migrations[^1];
        var ns = ReadNamespace(last.MainFile) ?? "Migrations";
        var usings = migrations.SelectMany(m => m.Usings)
            .Append("Microsoft.EntityFrameworkCore.Migrations")
            .Where(u => !IsBoilerplateUsing(u))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(u => u.StartsWith("System", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(u => u, StringComparer.Ordinal)
            .ToList();

        var years = migrations.GroupBy(m => m.Id.Length >= 4 ? m.Id[..4] : "0000").ToList();

        // 1. One partial file per year with the original Up/Down bodies.
        foreach (var year in years)
        {
            FileSystem.WriteText(Path.Combine(folder, $"{SquashedClassName}.{year.Key}.cs"), YearFile(ns, usings, year.Key, year.ToList()));
        }

        // 2. The migration entry point.
        FileSystem.WriteText(Path.Combine(folder, $"{SquashedClassName}.cs"), MainFile(ns, usings, migrations, years.Select(y => y.Key).ToList()));

        // 3. The designer of the last migration, renamed: same [Migration] id, same target model.
        var designerSource = last.DesignerFile ?? last.MainFile;
        var designer = RenameDesigner(File.ReadAllText(designerSource), last.ClassName);
        FileSystem.WriteText(Path.Combine(folder, $"{SquashedClassName}.Designer.cs"), designer);

        // 4. Remove the originals (their code now lives in the year files).
        var removedBytes = 0L;
        foreach (var file in migrations.SelectMany(m => new[] { m.MainFile, m.DesignerFile }).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            removedBytes += new FileInfo(file).Length;
            File.Delete(file);
        }

        StripBoilerplateUsings(context, [snapshot]);
        FileSystem.WriteText(Path.Combine(folder, HistoryFile), HistoryMarkdown(migrations));
        FileSystem.WriteText(Path.Combine(folder, PrecheckFile), PrecheckSql(last.Id));

        foreach (var migration in migrations.Where(m => m.ExtraMembers.Count > 0))
        {
            context.Report.Todo(Name, $"Migration {migration.Id} declares extra members ({string.Join(", ", migration.ExtraMembers)}) that were not copied " +
                                      "into the squashed migration; add them to the year file if its Up/Down uses them.", relativeFolder);
        }

        context.Report.Change(Name, $"Squashed {migrations.Count} migrations ({migrations[0].Id} … {last.Id}) into {SquashedClassName} " +
                                    $"({years.Count} files, {removedBytes / 1024 / 1024.0:0.0} MB of migration/designer code removed)", relativeFolder);
        context.Report.Todo(Name, $"Before deploying, every existing database must be at migration '{last.Id}' (run {PrecheckFile}). " +
                                  "The squashed migration keeps that id, so those databases see it as already applied.", relativeFolder);
        context.Report.Todo(Name, "Create the ABP.io schema changes with: dotnet ef migrations add AbpIoMigration — then review it: " +
                                  "Module Zero tables (AbpUsers, AbpRoles, AbpTenants, AbpSettings...) share names with ABP.io tables but have " +
                                  "a different schema (long → Guid ids), so a data migration script is needed for live databases.", relativeFolder);
        context.Log.WriteLine($"    squashed {migrations.Count} migrations into {years.Count + 2} files");
    }

    /// <summary>
    /// Migrations added to the source after the squash point: removed from the output, the snapshot is reset to the model
    /// of the squash point (its designer), and each one is reported (with a warning when it runs custom SQL, which
    /// "dotnet ef migrations add" cannot reproduce).
    /// </summary>
    private void ReportLaterMigrations(MigrationContext context, string folder, string snapshot, MigrationSource squashPoint, List<MigrationSource> later)
    {
        foreach (var migration in later)
        {
            foreach (var file in new[] { migration.MainFile, migration.DesignerFile }.OfType<string>().Where(File.Exists))
            {
                File.Delete(file);
            }

            var customSql = migration.UpBody.Contains(".Sql(", StringComparison.Ordinal);
            context.Report.Todo(Name, $"Source migration {migration.Id} ({migration.ClassName}) is newer than the squash point {squashPoint.Id}: " +
                                      "its model change comes with the converted entities — create it in the working branch with " +
                                      $"'dotnet ef migrations add {migration.ClassName}'" +
                                      (customSql ? "; it also runs custom SQL (migrationBuilder.Sql): copy that SQL into the new migration." : "."),
                context.Relative(folder));
        }

        // The snapshot of the squash point: the model the target database has after the squashed migration.
        var designer = File.ReadAllText(squashPoint.DesignerFile ?? squashPoint.MainFile);
        var designerModel = CSharpSyntaxTree.ParseText(designer).GetCompilationUnitRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == "BuildTargetModel");
        var snapshotRoot = CSharpSyntaxTree.ParseText(File.ReadAllText(snapshot)).GetCompilationUnitRoot();
        var buildModel = snapshotRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == "BuildModel");
        if (designerModel?.Body is not null && buildModel?.Body is not null)
        {
            FileSystem.WriteText(snapshot, snapshotRoot.ReplaceNode(buildModel.Body, designerModel.Body).ToFullString());
        }

        context.Report.Change(Name, $"{later.Count} source migrations newer than the squash point {squashPoint.Id} were not squashed " +
                                    "(the model snapshot stays at the squash point)", context.Relative(folder));
    }

    private static (List<MigrationSource> Migrations, List<string> Skipped) LoadMigrations(string folder, string snapshot)
    {
        var classes = new Dictionary<string, (string? MainFile, string? DesignerFile, string? Id, MethodDeclarationSyntax? Up, MethodDeclarationSyntax? Down, List<string> Usings, List<string> Extra)>(StringComparer.Ordinal);
        var skipped = new List<string>();

        foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            if (string.Equals(file, snapshot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetCompilationUnitRoot();
            var declarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
            if (declarations.Count == 0)
            {
                skipped.Add(file);
                continue;
            }

            foreach (var cls in declarations)
            {
                var name = cls.Identifier.Text;
                var entry = classes.TryGetValue(name, out var existing) ? existing : (null, null, null, null, null, new List<string>(), new List<string>());

                var id = cls.AttributeLists.SelectMany(a => a.Attributes)
                    .FirstOrDefault(a => a.Name.ToString() is "Migration" or "MigrationAttribute")
                    ?.ArgumentList?.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal
                    ? literal.Token.ValueText
                    : null;

                var up = cls.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == "Up");
                var down = cls.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == "Down");
                if (up is not null)
                {
                    entry.MainFile = file;
                    entry.Up = up;
                    entry.Down = down;
                    entry.Usings.AddRange(root.Usings.Where(u => u.Name is not null && u.Alias is null && !u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)).Select(u => u.Name!.ToString()));
                    entry.Extra.AddRange(cls.Members
                        .Where(m => m is not MethodDeclarationSyntax { Identifier.Text: "Up" or "Down" or "BuildTargetModel" })
                        .Select(m => m switch
                        {
                            MethodDeclarationSyntax method => method.Identifier.Text + "()",
                            PropertyDeclarationSyntax property => property.Identifier.Text,
                            FieldDeclarationSyntax field => string.Join(",", field.Declaration.Variables.Select(v => v.Identifier.Text)),
                            _ => m.Kind().ToString()
                        }));
                }

                if (id is not null)
                {
                    entry.Id = id;
                    if (cls.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.Text == "BuildTargetModel") || entry.DesignerFile is null)
                    {
                        entry.DesignerFile = up is null ? file : entry.DesignerFile;
                    }
                }

                classes[name] = entry;
            }
        }

        var migrations = new List<MigrationSource>();
        foreach (var (name, entry) in classes)
        {
            if (entry.Id is null || entry.MainFile is null || entry.Up is null)
            {
                foreach (var file in new[] { entry.MainFile, entry.DesignerFile }.OfType<string>()) skipped.Add(file);
                continue;
            }

            migrations.Add(new MigrationSource(entry.Id, name, entry.MainFile, entry.DesignerFile, Body(entry.Up), entry.Down is null ? "" : Body(entry.Down),
                entry.Usings, entry.Extra));
        }

        migrations.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return (migrations, skipped.Distinct().ToList());
    }

    /// <summary>The statements of Up/Down exactly as written (comments and formatting preserved).</summary>
    private static string Body(MethodDeclarationSyntax method)
    {
        var parameter = method.ParameterList.Parameters.FirstOrDefault()?.Identifier.Text ?? "migrationBuilder";
        var prefix = parameter == "migrationBuilder" ? "" : $"                var {parameter} = migrationBuilder;{Environment.NewLine}";

        if (method.ExpressionBody is not null)
        {
            return prefix + "                " + method.ExpressionBody.Expression.ToString() + ";" + Environment.NewLine;
        }

        var body = method.Body!;
        var text = string.Concat(body.Statements.Select(s => s.ToFullString())) + body.CloseBraceToken.LeadingTrivia.ToFullString();
        return prefix + text.TrimEnd() + Environment.NewLine;
    }

    private static string YearFile(string ns, List<string> usings, string year, List<MigrationSource> migrations)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine($"//   Generated by AbpToVolo: the original EF Core migrations of {year}, merged in their original order.");
        sb.AppendLine("//   Every block is the unchanged body of one original migration.");
        sb.AppendLine("// </auto-generated>");
        foreach (var u in usings) sb.AppendLine($"using {u};");
        sb.AppendLine();
        sb.AppendLine("#nullable disable");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns}");
        sb.AppendLine("{");
        sb.AppendLine($"    public partial class {SquashedClassName}");
        sb.AppendLine("    {");
        sb.AppendLine($"        private void Up{year}(MigrationBuilder migrationBuilder)");
        sb.AppendLine("        {");
        foreach (var migration in migrations)
        {
            AppendBlock(sb, migration.Id, migration.UpBody);
        }

        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine($"        private void Down{year}(MigrationBuilder migrationBuilder)");
        sb.AppendLine("        {");
        foreach (var migration in Enumerable.Reverse(migrations))
        {
            AppendBlock(sb, migration.Id, migration.DownBody);
        }

        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void AppendBlock(StringBuilder sb, string id, string body)
    {
        sb.AppendLine($"            // ==== {id} ====");
        sb.AppendLine("            {");
        sb.Append(body);
        sb.AppendLine("            }");
        sb.AppendLine();
    }

    private static string MainFile(string ns, List<string> usings, List<MigrationSource> migrations, List<string> years)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine($"//   Generated by AbpToVolo: {migrations.Count} EF Core migrations ({migrations[0].Id} … {migrations[^1].Id}) merged into one.");
        sb.AppendLine($"//   The migration id of the last original migration is kept (see {SquashedClassName}.Designer.cs), so databases that");
        sb.AppendLine($"//   already applied it treat this migration as applied. See {HistoryFile} for the merged migrations.");
        sb.AppendLine("// </auto-generated>");
        foreach (var u in usings) sb.AppendLine($"using {u};");
        sb.AppendLine();
        sb.AppendLine("#nullable disable");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns}");
        sb.AppendLine("{");
        sb.AppendLine($"    public partial class {SquashedClassName} : Migration");
        sb.AppendLine("    {");
        sb.AppendLine("        protected override void Up(MigrationBuilder migrationBuilder)");
        sb.AppendLine("        {");
        foreach (var year in years) sb.AppendLine($"            Up{year}(migrationBuilder);");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        protected override void Down(MigrationBuilder migrationBuilder)");
        sb.AppendLine("        {");
        foreach (var year in Enumerable.Reverse(years)) sb.AppendLine($"            Down{year}(migrationBuilder);");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    internal static string RenameDesigner(string designer, string oldClassName)
    {
        var root = CSharpSyntaxTree.ParseText(designer).GetCompilationUnitRoot();
        var cls = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First(c => c.Identifier.Text == oldClassName);
        var renamed = root.ReplaceToken(cls.Identifier, SyntaxFactory.Identifier(cls.Identifier.LeadingTrivia, SquashedClassName, cls.Identifier.TrailingTrivia));
        return StripUsings(renamed.ToFullString());
    }

    private static string HistoryMarkdown(List<MigrationSource> migrations)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# EF Core migration history");
        sb.AppendLine();
        sb.AppendLine($"AbpToVolo merged **{migrations.Count}** migrations into `{SquashedClassName}`.");
        sb.AppendLine();
        sb.AppendLine($"- The merged migration keeps the id `{migrations[^1].Id}` (the last original migration).");
        sb.AppendLine("  Databases that already applied it treat the merged migration as applied, and their `__EFMigrationsHistory` rows stay unchanged.");
        sb.AppendLine("- A new database runs the original `Up()` bodies in the order below and ends up with the same schema as before.");
        sb.AppendLine($"- A database that is NOT yet at `{migrations[^1].Id}` must first be updated with the old solution (check with `{PrecheckFile}`).");
        sb.AppendLine("- The original files are still available in source control history.");
        sb.AppendLine();
        sb.AppendLine("| # | Migration id | Original class |");
        sb.AppendLine("|---|---|---|");
        for (var i = 0; i < migrations.Count; i++)
        {
            sb.AppendLine($"| {i + 1} | `{migrations[i].Id}` | {migrations[i].ClassName} |");
        }

        return sb.ToString();
    }

    private static string PrecheckSql(string lastId) => $"""
        -- Generated by AbpToVolo.
        -- Run on every existing database before deploying the ABP.io version.
        -- The squashed migration keeps the id of the last Boilerplate migration; a database that has not applied that
        -- migration yet must be updated with the old solution first, otherwise EF Core would run the whole history again.
        IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'{lastId}')
            RAISERROR(N'Database is not at migration {lastId}. Update it with the Boilerplate solution before switching to ABP.io.', 16, 1);
        ELSE
            PRINT N'OK: database is at the squashed migration point ({lastId}).';

        """;

    private static string? ReadNamespace(string file) =>
        CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetCompilationUnitRoot()
            .DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();

    private static bool IsBoilerplateUsing(string ns) =>
        ns == "Abp" || ns.StartsWith("Abp.", StringComparison.Ordinal) || ns.StartsWith("Castle.", StringComparison.Ordinal);

    /// <summary>Snapshots/designers sometimes import Abp namespaces they do not need; those packages are gone.</summary>
    private void StripBoilerplateUsings(MigrationContext context, IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var stripped = StripUsings(text);
            if (stripped != text)
            {
                File.WriteAllText(file, stripped);
                context.Report.Change(Name, "Removed Abp.*/Castle.* usings", context.Relative(file));
            }
        }
    }

    private static string StripUsings(string text) =>
        string.Join("\n", text.Split('\n').Where(line =>
        {
            var t = line.Trim();
            return !(t.StartsWith("using Abp", StringComparison.Ordinal) && (t.StartsWith("using Abp;", StringComparison.Ordinal) || t.StartsWith("using Abp.", StringComparison.Ordinal)))
                   && !t.StartsWith("using Castle.", StringComparison.Ordinal);
        }));
}
