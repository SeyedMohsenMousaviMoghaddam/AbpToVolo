using AbpToVolo.Core;

namespace AbpToVolo.Steps;

/// <summary>Finds the solution name (Acme.PhoneBook) and the projects to migrate.</summary>
public sealed class DetectSolutionStep : IMigrationStep
{
    public string Name => "Detect solution";

    public void Execute(MigrationContext context)
    {
        context.RefreshProjects();
        if (context.Projects.Count == 0)
        {
            throw new MigrationException("No .csproj files were found in the input folder.");
        }

        context.SolutionName = context.Options.SolutionName ?? DetectName(context);
        context.ModulePrefix = DetectModulePrefix(context);

        foreach (var project in context.Projects)
        {
            context.Report.Info(Name, $"{project.Name} → layer {project.Layer}", context.Relative(project.CsprojPath));
        }

        context.Report.Info(Name, $"Solution name: {context.SolutionName ?? "(unknown)"}");
        context.Log.WriteLine($"    solution '{context.SolutionName}', {context.Projects.Count} projects");
    }

    /// <summary>"PhoneBookCoreModule.cs" → "PhoneBook", so generated modules are named consistently (PhoneBookDomainSharedModule).</summary>
    private static string DetectModulePrefix(MigrationContext context)
    {
        var domain = context.FindProject(ProjectLayer.Domain);
        var moduleFile = domain is null
            ? null
            : FileSystem.EnumerateFiles(domain.Directory, "*Module.cs")
                .Select(Path.GetFileNameWithoutExtension)
                .FirstOrDefault(n => n!.EndsWith("CoreModule", StringComparison.Ordinal) || n.EndsWith("DomainModule", StringComparison.Ordinal));

        if (moduleFile is not null)
        {
            return moduleFile.EndsWith("CoreModule", StringComparison.Ordinal) ? moduleFile[..^"CoreModule".Length] : moduleFile[..^"DomainModule".Length];
        }

        var last = (context.SolutionName ?? "App").Split('.').Last();
        return new string(last.Where(char.IsLetterOrDigit).ToArray()) is { Length: > 0 } clean ? clean : "App";
    }

    private static string? DetectName(MigrationContext context)
    {
        var solution = FileSystem.EnumerateFiles(context.OutputRoot, "*.sln")
            .Concat(FileSystem.EnumerateFiles(context.OutputRoot, "*.slnx"))
            .OrderBy(f => f.Length)
            .FirstOrDefault();
        if (solution is not null)
        {
            return Path.GetFileNameWithoutExtension(solution);
        }

        var core = context.Projects.FirstOrDefault(p => p.Name.EndsWith(".Core", StringComparison.OrdinalIgnoreCase));
        return core?.Name[..^".Core".Length];
    }
}
