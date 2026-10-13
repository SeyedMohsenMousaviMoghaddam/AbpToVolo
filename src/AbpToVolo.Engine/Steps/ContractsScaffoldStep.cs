using AbpToVolo.Core;

namespace AbpToVolo.Steps;

/// <summary>
/// Creates the *.Domain.Shared and *.Application.Contracts projects of the ABP.io layered template and wires the
/// project references (Domain → Domain.Shared, Application.Contracts → Domain.Shared, Application → Application.Contracts).
/// Runs before the localization step so the JSON resources are generated directly in Domain.Shared.
/// Moving code into these projects is done later by <see cref="ContractsStep"/>.
/// </summary>
public sealed class ContractsScaffoldStep : IMigrationStep
{
    public string Name => "Contract projects";

    public bool IsEnabled(MigrationOptions options) => options.CreateContracts;

    public static string BaseName(ProjectInfo domain) =>
        domain.Name.EndsWith(".Core", StringComparison.Ordinal) ? domain.Name[..^".Core".Length]
        : domain.Name.EndsWith(".Domain", StringComparison.Ordinal) ? domain.Name[..^".Domain".Length]
        : domain.Name;

    public void Execute(MigrationContext context)
    {
        var domain = context.FindProject(ProjectLayer.Domain);
        var application = context.FindProject(ProjectLayer.Application);
        if (domain is null || application is null)
        {
            context.Report.Warning(Name, "Domain (Core) or Application project not found; contract projects were not created.");
            return;
        }

        var baseName = BaseName(domain);
        var parent = Path.GetDirectoryName(domain.Directory)!;
        var settings = ProjectWriter.CompileSettings(domain.CsprojPath);
        var solution = ProjectWriter.FindSolution(context.OutputRoot);

        var shared = context.FindProject(ProjectLayer.DomainShared)?.CsprojPath ?? Create(context, solution, parent, baseName + ".Domain.Shared", domain.RootNamespace, [], settings);
        var contracts = context.FindProject(ProjectLayer.ApplicationContracts)?.CsprojPath ?? Create(context, solution, parent, baseName + ".Application.Contracts", domain.RootNamespace, [shared], settings);

        AddReference(context, domain.CsprojPath, shared);
        AddReference(context, application.CsprojPath, contracts);
        if (context.FindProject(ProjectLayer.HttpApi) is { } httpApi)
        {
            AddReference(context, httpApi.CsprojPath, contracts);
        }

        context.RefreshProjects();
    }

    private string Create(MigrationContext context, string? solution, string parent, string name, string rootNamespace, string[] references, Dictionary<string, string> settings)
    {
        var path = ProjectWriter.CreateProject(Path.Combine(parent, name), name, context.Options.TargetFramework, rootNamespace, references, properties: settings);
        context.Report.Change(Name, $"Created project {name}", context.Relative(path));

        if (solution is not null && ProjectWriter.AddToSolution(solution, path) is { } error)
        {
            context.Report.Warning(Name, $"Could not add {name} to the solution: {error}");
        }

        return path;
    }

    private void AddReference(MigrationContext context, string from, string to)
    {
        if (ProjectWriter.AddProjectReference(from, to))
        {
            context.Report.Change(Name, $"Added reference to {Path.GetFileNameWithoutExtension(to)}", context.Relative(from));
        }
    }
}
