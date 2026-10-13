using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AbpToVolo.Core;

namespace AbpToVolo.Steps;

/// <summary>Writes ABP-MIGRATION-REPORT.md (for people) and abp-migration-report.json (for tools) to the output root.</summary>
public sealed class ReportWriterStep : IMigrationStep
{
    public const string MarkdownFile = "ABP-MIGRATION-REPORT.md";
    public const string JsonFile = "abp-migration-report.json";

    public string Name => "Report";

    public void Execute(MigrationContext context)
    {
        context.State.ToolVersion = typeof(ReportWriterStep).Assembly.GetName().Version?.ToString();
        context.State.Save(context.OutputRoot);
        WriteGitIgnore(context);

        var report = context.Report;
        var entries = report.Entries;
        var todos = entries.Where(e => e.Level is ReportLevel.Todo or ReportLevel.Warning).ToList();
        var changes = entries.Where(e => e.Level == ReportLevel.Change).ToList();

        var md = new StringBuilder();
        md.AppendLine("# ABP.io migration report");
        md.AppendLine();
        md.AppendLine("| | |");
        md.AppendLine("|---|---|");
        md.AppendLine($"| Source | `{context.Options.InputPath}` |");
        md.AppendLine($"| Solution | `{context.SolutionName}` |");
        md.AppendLine($"| ABP version | `{context.Options.AbpVersion}` |");
        md.AppendLine($"| Target framework | `{context.Options.TargetFramework}` |");
        md.AppendLine($"| Projects migrated | {report.ProjectsMigrated} |");
        md.AppendLine($"| C# files changed | {report.CSharpFilesChanged} / {report.CSharpFilesScanned} |");
        md.AppendLine($"| Automatic changes | {changes.Count} |");
        md.AppendLine($"| Items needing manual work | {todos.Count} |");
        if (report.BuildErrorCount is { } errorCount)
        {
            md.AppendLine($"| Build errors | {errorCount} |");
        }

        md.AppendLine();
        md.AppendLine("## Projects");
        md.AppendLine();
        md.AppendLine("| Project | Layer | Module Zero |");
        md.AppendLine("|---|---|---|");
        foreach (var project in context.Projects)
        {
            md.AppendLine($"| {project.Name} | {project.Layer} | {(context.ProjectsUsingZero.Contains(project.Name) ? "yes" : "")} |");
        }

        foreach (var (title, lines) in report.Sections)
        {
            md.AppendLine();
            md.AppendLine($"## {title}");
            md.AppendLine();
            foreach (var line in lines) md.AppendLine(line.StartsWith("- ", StringComparison.Ordinal) ? line : line + "  ");
        }

        md.AppendLine();
        md.AppendLine("## Manual work");
        md.AppendLine();
        if (todos.Count == 0)
        {
            md.AppendLine("Nothing left. 🎉");
        }

        foreach (var group in todos.GroupBy(t => t.File ?? "(solution)").OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            md.AppendLine($"### `{group.Key}`");
            md.AppendLine();
            foreach (var todo in group.OrderBy(t => t.Line ?? 0).DistinctBy(t => (t.Line, t.Message)))
            {
                var location = todo.Line is { } line && todo.File is not null ? $"[L{line}]({todo.File}#L{line}) " : "";
                var prefix = todo.Level == ReportLevel.Warning ? "⚠️ " : "";
                md.AppendLine($"- [ ] {prefix}{location}{todo.Message}");
            }

            md.AppendLine();
        }

        if (report.BuildErrors.Count > 0)
        {
            md.AppendLine("## Build errors");
            md.AppendLine();
            md.AppendLine("```text");
            foreach (var error in report.BuildErrors.Take(300)) md.AppendLine(error);
            if (report.BuildErrors.Count > 300) md.AppendLine($"... {report.BuildErrors.Count - 300} more");
            md.AppendLine("```");
            md.AppendLine();
        }

        md.AppendLine("## Next steps");
        md.AppendLine();
        foreach (var step in NextSteps(context))
        {
            md.AppendLine($"- [ ] {step}");
        }

        md.AppendLine();
        md.AppendLine("## Automatic changes");
        md.AppendLine();
        foreach (var group in changes.GroupBy(c => c.File ?? $"({c.Step})").OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            md.AppendLine($"<details><summary><code>{group.Key}</code> ({group.Count()})</summary>");
            md.AppendLine();
            foreach (var change in group.Select(c => c.Message).Distinct()) md.AppendLine($"- {change}");
            md.AppendLine();
            md.AppendLine("</details>");
        }

        FileSystem.WriteText(Path.Combine(context.OutputRoot, MarkdownFile), md.ToString());

        var json = JsonSerializer.Serialize(new
        {
            context.SolutionName,
            context.Options.AbpVersion,
            context.Options.TargetFramework,
            report.ProjectsMigrated,
            report.CSharpFilesScanned,
            report.CSharpFilesChanged,
            report.BuildErrorCount,
            Projects = context.Projects.Select(p => new { p.Name, Layer = p.Layer.ToString(), UsesZero = context.ProjectsUsingZero.Contains(p.Name) }),
            Entries = entries
        }, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        FileSystem.WriteText(Path.Combine(context.OutputRoot, JsonFile), json);

        context.Log.WriteLine($"    {MarkdownFile}: {changes.Count} changes, {todos.Count} manual items");
    }

    private static IEnumerable<string> NextSteps(MigrationContext context)
    {
        yield return "Open the solution, restore packages and fix the remaining compiler errors (start with the Domain/Core project).";
        if (context.ProjectsUsingZero.Count > 0)
        {
            yield return "Module Zero → ABP.io modules: remove User/Role/Tenant/*Manager/*Store classes, use IdentityUser/IdentityRole/Tenant " +
                         "(extend with module entity extensions), and replace UserAppService/RoleAppService/TenantAppService with the Identity/TenantManagement application modules if they are standard CRUD.";
            yield return "User and tenant ids are Guid in ABP.io (long/int in Boilerplate): update DTOs, foreign keys and client code.";
        }

        yield return context.Options.Migrations switch
        {
            MigrationsMode.Squash => "Migrations were squashed (see Migrations/MIGRATION-HISTORY.md): run squash-precheck.sql on every database, then " +
                                     "create the ABP.io schema changes with dotnet ef migrations add AbpIoMigration and review them.",
            MigrationsMode.Delete => "Create a fresh EF Core migration (dotnet ef migrations add Initial) and plan a data migration script for existing databases.",
            _ => "The old migrations were kept: create the ABP.io schema changes with dotnet ef migrations add AbpIoMigration and review them."
        };
        yield return "Replace the Boilerplate token auth (TokenAuthController / JWT) with OpenIddict (Volo.Abp.Account + OpenIddict modules) or keep JWT bearer manually.";
        yield return "Move the Startup/Program pipeline into the host module (ConfigureServices / OnApplicationInitialization) following the ABP.io HttpApi.Host template.";
        if (!context.Options.CreateContracts)
        {
            yield return "Optionally split contracts (run again without --no-contracts): *.Domain.Shared and *.Application.Contracts as in the ABP.io layered template.";
        }
        else
        {
            yield return "Review the types that stayed in Core/Application (section 'Contract projects') and move them when their dependencies allow it.";
        }
        yield return "Frontend (Angular/Vue/MVC) is not converted: regenerate service proxies with `abp generate-proxy` and adapt to ABP.io's /api/abp/application-configuration.";
        yield return context.Options.CreateTests
            ? "Run the generated tests (dotnet test test/*.EntityFrameworkCore.Tests) and add real scenarios to the abstract test classes."
            : "Run the unit/integration tests (AbpIntegratedTest<TModule>) and fix the test base classes.";
    }

    /// <summary>
    /// .gitignore for the output (git update workflow): build outputs and the files that differ on every run (log, json report).
    /// Entries are only added; an existing .gitignore of the source is kept.
    /// </summary>
    private static void WriteGitIgnore(MigrationContext context)
    {
        string[] entries = ["bin/", "obj/", ".vs/", "*.user", "Logs/", FileLogObserver.FileName, JsonFile];
        var path = Path.Combine(context.OutputRoot, ".gitignore");
        var existing = File.Exists(path) ? File.ReadAllLines(path) : [];
        var known = existing.Select(l => l.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = entries.Where(e => !known.Contains(e) && !known.Contains(e.TrimEnd('/')) && !known.Contains("[" + char.ToUpperInvariant(e[0]) + char.ToLowerInvariant(e[0]) + "]" + e[1..])).ToList();
        if (missing.Count == 0) return;

        var lines = existing.ToList();
        if (lines.Count > 0) lines.Add("");
        lines.Add("# AbpToVolo (build outputs and per-run files)");
        lines.AddRange(missing);
        File.WriteAllLines(path, lines);
    }
}
