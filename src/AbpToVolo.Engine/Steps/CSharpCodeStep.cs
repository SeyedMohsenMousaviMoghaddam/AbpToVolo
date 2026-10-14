using AbpToVolo.CodeMigration;
using AbpToVolo.Core;

namespace AbpToVolo.Steps;

/// <summary>Rewrites every C# file of the solution with the Roslyn rewriter pipeline.</summary>
public sealed class CSharpCodeStep : IMigrationStep
{
    public string Name => "C# code";

    public void Execute(MigrationContext context)
    {
        var files = context.Projects
            .SelectMany(p => FileSystem.EnumerateFiles(p.Directory, "*.cs"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => !context.IsExcluded(f))
            .ToList();

        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            context.Report.CSharpFilesScanned++;
            var relative = context.Relative(file);
            context.Progress(index + 1, files.Count, relative);
            var (code, encoding) = FileSystem.ReadText(file);
            if (CSharpFileMigrator.IsGenerated(code))
            {
                continue;
            }

            var project = context.FindProjectForFile(file);
            var fileContext = new FileMigrationContext(file, FileSystem.DetectNewLine(code), project, context.PrimaryLocalization) { IdType = context.Options.IdType, ApiStyle = context.Options.ApiStyle };

            CSharpMigrationResult result;
            try
            {
                result = CSharpFileMigrator.Migrate(code, fileContext);
            }
            catch (Exception ex)
            {
                context.Report.Warning(Name, $"Conversion failed, file left unchanged: {ex.Message}", relative);
                continue;
            }

            if (fileContext.UsesZero && project is not null)
            {
                context.ProjectsUsingZero.Add(project.Name);
            }

            foreach (var todo in fileContext.FileTodos)
            {
                context.Report.Todo(Name, todo, relative);
            }

            if (!result.Changed)
            {
                continue;
            }

            context.Report.CSharpFilesChanged++;
            FileSystem.WriteText(file, result.Code, encoding);
            foreach (var change in fileContext.Changes)
            {
                context.Report.Change(Name, change, relative);
            }

            context.Verbose($"{relative}: {fileContext.Changes.Count} changes");
        }

        context.Log.WriteLine($"    {context.Report.CSharpFilesChanged}/{context.Report.CSharpFilesScanned} files changed");
    }
}
