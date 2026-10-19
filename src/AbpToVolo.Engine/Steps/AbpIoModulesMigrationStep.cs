using System.Diagnostics;
using System.Text.RegularExpressions;
using AbpToVolo.CodeMigration;
using AbpToVolo.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.Steps;

/// <summary>
/// Last database step of the Module Zero replacement: the migration that creates the ABP.io module tables.
/// After the build, "dotnet ef migrations add AbpIoModules" is run in the EF Core project (it does not connect to the
/// database), then the migration is made safe for an existing database:
///   - the DropTable("Zero_*") calls are removed (the Module Zero data stays until it has been checked; drop the
///     Zero_* tables later), and the matching CreateTable("Zero_*") calls of Down();
///   - ModuleZeroDataMigration.sql runs after the ABP.io tables are created and before the foreign keys from the
///     application's tables to AbpUsers/AbpRoles are added (those rows must exist first).
/// The migration id is kept in state.json, so re-converting gives the same files. Needs the dotnet-ef tool; without
/// it (or when the build failed) the steps are listed in the report instead.
/// </summary>
public sealed partial class AbpIoModulesMigrationStep : IMigrationStep
{
    public const string MigrationName = "AbpIoModules";

    public string Name => "ABP.io modules migration";

    public bool IsEnabled(MigrationOptions options) => options.ConvertModuleZero && options.CompilerFixes && options.Migrations == MigrationsMode.Squash;

    public void Execute(MigrationContext context)
    {
        var efCore = context.FindProject(ProjectLayer.EntityFrameworkCore);
        var folder = efCore is null ? null : FileSystem.EnumerateFiles(efCore.Directory, "*ModelSnapshot.cs").Select(Path.GetDirectoryName).FirstOrDefault();
        if (efCore is null || folder is null || !context.UsesIdentityModules ||
            !Directory.EnumerateFiles(folder, $"*_{ZeroDatabase.RenameMigrationName}.cs").Any())
        {
            return;
        }

        if (Directory.EnumerateFiles(folder, $"*_{MigrationName}.cs").Any())
        {
            context.Report.Info(Name, $"{MigrationName} migration already exists.");
            return;
        }

        context.Log.WriteLine($"    dotnet ef migrations add {MigrationName} ...");
        var (exitCode, output) = Run("dotnet", ["ef", "migrations", "add", MigrationName, "--project", efCore.CsprojPath], efCore.Directory, context.CancellationToken);
        var migration = Directory.EnumerateFiles(folder, $"*_{MigrationName}.cs").FirstOrDefault(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal));
        if (exitCode != 0 || migration is null)
        {
            var reason = output.Contains("Could not execute because the specified command or file was not found", StringComparison.Ordinal) ||
                         output.Contains("dotnet-ef", StringComparison.Ordinal)
                ? "the dotnet-ef tool is not installed (dotnet tool install -g dotnet-ef)"
                : "dotnet ef failed: " + string.Join(" | ", EfErrors(output));
            context.Report.Todo(Name, $"{MigrationName} was not generated ({reason}). Run 'dotnet ef migrations add {MigrationName}' in {efCore.Name}, remove its " +
                                      $"DropTable(\"Zero_*\") calls and run {ZeroDatabase.DataScript} before its AddForeignKey calls.", context.Relative(efCore.CsprojPath));
            return;
        }

        var dataScript = Path.Combine(efCore.Directory, ZeroDatabase.DataScript);
        var (code, encoding) = FileSystem.ReadText(migration);
        var snapshot = FileSystem.EnumerateFiles(folder, "*ModelSnapshot.cs").First();
        var views = ToViewName().Matches(File.ReadAllText(snapshot)).Select(m => m.Groups["view"].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (updated, dropped) = MakeSafe(code, File.Exists(dataScript) ? File.ReadAllText(dataScript) : null, views);
        FileSystem.WriteText(migration, updated, encoding);
        KeepStableId(context, migration);

        context.Report.Change(Name, $"Generated migration {MigrationName}: ABP.io module tables; {dropped} DropTable(\"Zero_*\") calls removed; " +
                                    $"{ZeroDatabase.DataScript} runs before the new foreign keys", context.Relative(folder));
        context.Report.Todo(Name, "Database: back it up, check it with squash-precheck.sql, then run the DbMigrator (or 'dotnet ef database update'). " +
                                  "When the copied users/roles/permissions are verified, drop the Zero_* tables.", context.Relative(folder));
    }

    /// <summary>The error lines of the dotnet ef output (exceptions, errors), or its last line.</summary>
    private static IEnumerable<string> EfErrors(string output)
    {
        var lines = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var errors = lines.Where(l => !l.Contains(": warning ", StringComparison.Ordinal) &&
                                      (l.Contains("rror", StringComparison.Ordinal) || l.Contains("Exception", StringComparison.Ordinal) ||
                                       l.StartsWith("Unable to", StringComparison.Ordinal) || l.StartsWith("The ", StringComparison.Ordinal)))
            .Take(3).ToList();
        return errors.Count > 0 ? errors : lines.TakeLast(1);
    }

    /// <summary>Removes the Zero_* table drops (Up) and re-creations (Down); inserts the data script before the first AddForeignKey.</summary>
    internal static (string Code, int Dropped) MakeSafe(string code, string? dataSql, IReadOnlySet<string>? views = null)
    {
        var root = CSharpSyntaxTree.ParseText(code).GetCompilationUnitRoot();
        var up = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First(m => m.Identifier.Text == "Up");
        var down = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == "Down");

        static string? Target(StatementSyntax s, string[] methods) =>
            s is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: var name } } invocation } &&
            methods.Contains(name)
                ? invocation.ArgumentList.Arguments
                    .Where(a => a.NameColon?.Name.Identifier.Text == (name is "CreateTable" or "DropTable" ? "name" : "table"))
                    .Select(a => (a.Expression as LiteralExpressionSyntax)?.Token.ValueText).FirstOrDefault()
                : null;

        static bool OnZeroTable(StatementSyntax s, params string[] methods) =>
            Target(s, methods) is { } table && table.StartsWith("Zero_", StringComparison.Ordinal);

        // Entities mapped to SQL views (ToView): EF must not alter them as tables (the views are recreated by Zero_RecreateViews).
        string[] tableOperations = ["AlterColumn", "AddColumn", "DropColumn", "RenameColumn", "CreateIndex", "DropIndex", "AddForeignKey", "DropForeignKey", "AddPrimaryKey", "DropPrimaryKey", "CreateTable", "DropTable"];
        bool OnView(StatementSyntax s) => views is { Count: > 0 } && Target(s, tableOperations) is { } table && views.Contains(table);

        var upDrops = up.Body!.Statements.Where(s => OnZeroTable(s, "DropTable")).ToList();
        var downCreates = down?.Body?.Statements.Where(s => OnZeroTable(s, "CreateTable", "CreateIndex", "AddForeignKey") || OnView(s)).ToList() ?? [];

        var newUp = up.Body.Statements.Where(s => !upDrops.Contains(s) && !OnView(s)).ToList();
        if (!string.IsNullOrWhiteSpace(dataSql))
        {
            var index = newUp.FindIndex(s => s is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "AddForeignKey" } } });
            if (index < 0) index = newUp.Count;
            var indent = newUp.Count > 0 ? SyntaxHelpers.Indentation(newUp[0]) : "            ";
            var nl = code.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var sql = ParseStatement($"migrationBuilder.Sql(@\"{dataSql.Replace("\"", "\"\"")}\");")
                .WithLeadingTrivia(Whitespace(indent), Comment($"// AbpToVolo: {ZeroDatabase.DataScript} — Module Zero users, roles, tenants and permissions → ABP.io tables " +
                                                              "(same ids as Zero_IdMap), before the foreign keys to them are added."), EndOfLine(nl), Whitespace(indent))
                .WithTrailingTrivia(EndOfLine(nl), EndOfLine(nl));
            newUp.Insert(index, sql);
        }

        // The views saved and dropped by RenameModuleZeroTables, now that AbpUsers/AbpRoles/... exist again.
        {
            var indent = newUp.Count > 0 ? SyntaxHelpers.Indentation(newUp[0]) : "            ";
            var nl = code.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            newUp.Add(ParseStatement("migrationBuilder.Sql(@\"IF OBJECT_ID(N'dbo.Zero_RecreateViews', N'P') IS NOT NULL EXEC dbo.Zero_RecreateViews;\");")
                .WithLeadingTrivia(EndOfLine(nl), Whitespace(indent), Comment("// AbpToVolo: recreates the views saved by RenameModuleZeroTables (audit column names updated)."),
                    EndOfLine(nl), Whitespace(indent))
                .WithTrailingTrivia(EndOfLine(nl)));
            newUp.Add(ParseStatement("migrationBuilder.Sql(@\"IF OBJECT_ID(N'dbo.Zero_ConvertModules', N'P') IS NOT NULL EXEC dbo.Zero_ConvertModules;\");")
                .WithLeadingTrivia(Whitespace(indent), Comment("// AbpToVolo: procedures/functions recreated for the ABP.io columns (audit names, Guid TenantId compared with an int @TenantId)."),
                    EndOfLine(nl), Whitespace(indent))
                .WithTrailingTrivia(EndOfLine(nl)));
        }

        var replacements = new Dictionary<SyntaxNode, SyntaxNode> { [up.Body] = up.Body.WithStatements(List(newUp)) };
        if (down?.Body is not null && downCreates.Count > 0)
        {
            replacements[down.Body] = down.Body.WithStatements(List(down.Body.Statements.Where(s => !downCreates.Contains(s))));
        }

        var result = root.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);
        var note = $"// AbpToVolo: the Zero_* tables (Module Zero) are not dropped here: drop them once the copied data is verified.{Environment.NewLine}";
        return (note + result.ToFullString(), upDrops.Count);
    }

    /// <summary>Same migration id on every conversion (state.json): the generated files are renamed to the first id.</summary>
    private static void KeepStableId(MigrationContext context, string migration)
    {
        var folder = Path.GetDirectoryName(migration)!;
        var id = Path.GetFileName(migration)[..14];
        if (!context.State.MigrationIds.TryGetValue(MigrationName, out var stable))
        {
            context.State.MigrationIds[MigrationName] = id;
            return;
        }

        if (stable == id) return;
        foreach (var file in Directory.EnumerateFiles(folder, $"{id}_{MigrationName}*.cs"))
        {
            var text = File.ReadAllText(file).Replace($"\"{id}_{MigrationName}\"", $"\"{stable}_{MigrationName}\"", StringComparison.Ordinal);
            var target = Path.Combine(folder, Path.GetFileName(file).Replace(id, stable, StringComparison.Ordinal));
            File.WriteAllText(target, text);
            File.Delete(file);
        }
    }

    private static (int ExitCode, string Output) Run(string file, IEnumerable<string> args, string directory, CancellationToken token)
    {
        try
        {
            var start = new ProcessStartInfo(file, args) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            process.WaitForExitAsync(token).GetAwaiter().GetResult();
            return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, e.Message);
        }
    }

    [GeneratedRegex(@"\.ToView\(\s*""(?<view>[^""]+)""")]
    private static partial Regex ToViewName();
}
