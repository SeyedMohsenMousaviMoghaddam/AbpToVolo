using System.Xml.Linq;

namespace AbpToVolo.Core;

public enum ProjectLayer
{
    Unknown,
    DomainShared,
    Domain,
    ApplicationContracts,
    Application,
    EntityFrameworkCore,
    HttpApi,
    Host,
    DbMigrator,
    Tests
}

public sealed class ProjectInfo
{
    public required string Name { get; init; }
    public required string CsprojPath { get; init; }
    public string Directory => Path.GetDirectoryName(CsprojPath)!;
    public required ProjectLayer Layer { get; init; }
    public required string RootNamespace { get; init; }

    public static ProjectInfo Load(string csprojPath)
    {
        var name = Path.GetFileNameWithoutExtension(csprojPath);
        var rootNamespace = name;
        try
        {
            var doc = XDocument.Load(csprojPath);
            rootNamespace = doc.Descendants("RootNamespace").FirstOrDefault()?.Value.Trim() is { Length: > 0 } rn
                ? rn
                : doc.Descendants("AssemblyName").FirstOrDefault()?.Value.Trim() is { Length: > 0 } an ? an : name;
        }
        catch
        {
            // Not a valid XML project file; keep the defaults.
        }

        return new ProjectInfo { Name = name, CsprojPath = csprojPath, Layer = DetectLayer(name), RootNamespace = rootNamespace };
    }

    public static ProjectLayer DetectLayer(string projectName)
    {
        static bool Ends(string n, string suffix) => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

        if (projectName.Contains(".Tests", StringComparison.OrdinalIgnoreCase) || Ends(projectName, ".Test")) return ProjectLayer.Tests;
        if (Ends(projectName, ".Web.Host") || Ends(projectName, ".HttpApi.Host") || Ends(projectName, ".Web.Mvc") || Ends(projectName, ".Web.Public")) return ProjectLayer.Host;
        if (Ends(projectName, ".Web.Core") || Ends(projectName, ".HttpApi")) return ProjectLayer.HttpApi;
        if (Ends(projectName, ".EntityFrameworkCore") || Ends(projectName, ".EntityFramework")) return ProjectLayer.EntityFrameworkCore;
        if (Ends(projectName, ".Application.Contracts")) return ProjectLayer.ApplicationContracts;
        if (Ends(projectName, ".Domain.Shared")) return ProjectLayer.DomainShared;
        if (Ends(projectName, ".Application")) return ProjectLayer.Application;
        if (Ends(projectName, ".Core") || Ends(projectName, ".Domain")) return ProjectLayer.Domain;
        if (Ends(projectName, ".Migrator") || Ends(projectName, ".DbMigrator")) return ProjectLayer.DbMigrator;
        return ProjectLayer.Unknown;
    }
}

public sealed record LocalizationResourceInfo(
    string SourceName,
    string TypeName,
    string Namespace,
    string VirtualPath,
    string DefaultCulture,
    string ProjectName,
    IReadOnlyList<string>? Cultures = null);

public sealed class MigrationContext
{
    public MigrationContext(MigrationOptions options, MigrationReport report, IMigrationObserver observer, CancellationToken cancellationToken = default)
    {
        Options = options;
        Report = report;
        Observer = observer;
        CancellationToken = cancellationToken;
        Log = new ObserverTextWriter(observer);
    }

    public MigrationOptions Options { get; }
    public MigrationReport Report { get; }
    public IMigrationObserver Observer { get; }
    public CancellationToken CancellationToken { get; }

    /// <summary>Summary lines of a step (shown by every front-end).</summary>
    public TextWriter Log { get; }

    /// <summary>The step that is running (set by the engine); used for progress reporting.</summary>
    internal StepInfo? CurrentStep { get; set; }

    /// <summary>Reports progress inside a long step and checks for cancellation.</summary>
    public void Progress(int current, int total, string? item = null)
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (CurrentStep is not null) Observer.StepProgress(CurrentStep, current, total, item);
    }

    public string OutputRoot => Options.OutputPath;
    public string? SolutionName { get; set; }

    /// <summary>Decisions of earlier conversions into the same output folder (loaded by CopySourceStep, saved by ReportWriterStep).</summary>
    public MigrationState State { get; set; } = new();

    /// <summary>Resolved by ResolveVersionsStep.</summary>
    public string MicrosoftPackagesVersion { get; set; } = "10.0.*";
    public List<ProjectInfo> Projects { get; private set; } = [];
    public List<LocalizationResourceInfo> LocalizationResources { get; } = [];

    /// <summary>Module Zero was replaced: the ABP.io Identity/TenantManagement/Account/OpenIddict modules are used.</summary>
    public bool UsesIdentityModules { get; set; }

    /// <summary>Former custom properties of Zero entities, now extra properties: ABP type name → (property → C# type).</summary>
    public Dictionary<string, Dictionary<string, string>> ExtensionProperties { get; } = new(StringComparer.Ordinal);

    /// <summary>Projects whose code (or packages) referenced Module Zero.</summary>
    public HashSet<string> ProjectsUsingZero { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The solution's own localization source (named like the solution/module prefix), else the first one.</summary>
    public LocalizationResourceInfo? PrimaryLocalization =>
        LocalizationResources.FirstOrDefault(l => string.Equals(l.SourceName, ModulePrefix, StringComparison.OrdinalIgnoreCase)) ??
        LocalizationResources.FirstOrDefault(l => SolutionName is not null && SolutionName.EndsWith(l.SourceName, StringComparison.OrdinalIgnoreCase)) ??
        LocalizationResources.FirstOrDefault(l => !l.SourceName.StartsWith("Abp", StringComparison.Ordinal)) ??
        LocalizationResources.FirstOrDefault();

    /// <summary>Folders (e.g. EF migrations) whose C# must not be rewritten or scanned.</summary>
    public HashSet<string> ExcludedDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsExcluded(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        return ExcludedDirectories.Any(d => full.StartsWith(d.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    public ProjectInfo? FindProject(ProjectLayer layer) =>
        Projects.FirstOrDefault(p => p.Layer == layer);

    /// <summary>Prefix of the module classes, taken from the existing domain module (e.g. "Acme.PhoneBook" → "PhoneBook").</summary>
    public string ModulePrefix { get; set; } = "App";

    public void RefreshProjects()
    {
        Projects = FileSystem.EnumerateFiles(OutputRoot, "*.csproj")
            .Select(ProjectInfo.Load)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ProjectInfo? FindProjectForFile(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        return Projects
            .Where(p => full.StartsWith(p.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Directory.Length)
            .FirstOrDefault();
    }

    public string Relative(string path) => Path.GetRelativePath(OutputRoot, path).Replace('\\', '/');

    public void Verbose(string message) => Observer.Log(LogLevel.Detail, message);
}
