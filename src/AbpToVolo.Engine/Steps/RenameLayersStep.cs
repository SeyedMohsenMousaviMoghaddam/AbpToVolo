using AbpToVolo.Core;

namespace AbpToVolo.Steps;

/// <summary>
/// Optional (--rename-layers): renames Boilerplate projects to ABP.io layer names and fixes every reference
/// in .sln/.slnx/.csproj/Dockerfile/scripts. C# namespaces are not changed.
/// </summary>
public sealed class RenameLayersStep : IMigrationStep
{
    private static readonly (string From, string To)[] Suffixes =
    [
        (".Web.Host", ".HttpApi.Host"),
        (".Web.Core", ".HttpApi"),
        (".Migrator", ".DbMigrator"),
        (".Core", ".Domain"),
    ];

    private static readonly string[] TextFilePatterns =
        ["*.sln", "*.slnx", "*.csproj", "*.props", "*.targets", "Dockerfile", "*.yml", "*.yaml", "*.ps1", "*.sh", "*.cmd", "*.bat"];

    public string Name => "Rename layers";

    public bool IsEnabled(MigrationOptions options) => options.RenameLayers;

    public void Execute(MigrationContext context)
    {
        var renames = new List<(string OldName, string NewName)>();

        foreach (var project in context.Projects)
        {
            var match = Suffixes.FirstOrDefault(s => project.Name.EndsWith(s.From, StringComparison.Ordinal));
            if (match.From is null)
            {
                continue;
            }

            var newName = project.Name[..^match.From.Length] + match.To;
            if (context.Projects.Any(p => p.Name == newName))
            {
                context.Report.Warning(Name, $"{project.Name} not renamed: {newName} already exists.");
                continue;
            }

            renames.Add((project.Name, newName));
            var newCsproj = Path.Combine(project.Directory, newName + ".csproj");
            File.Move(project.CsprojPath, newCsproj);

            if (Path.GetFileName(project.Directory) == project.Name)
            {
                var newDirectory = Path.Combine(Path.GetDirectoryName(project.Directory)!, newName);
                DeleteBuildLeftovers(newDirectory);
                Directory.Move(project.Directory, newDirectory);
            }

            context.Report.Change(Name, $"{project.Name} → {newName}");
        }

        if (renames.Count == 0)
        {
            return;
        }

        // Longest names first so "Acme.Web.Core" is not partially replaced by the "Acme.Core" rule.
        var ordered = renames.OrderByDescending(r => r.OldName.Length).ToList();
        foreach (var file in TextFilePatterns.SelectMany(p => FileSystem.EnumerateFiles(context.OutputRoot, p)).Distinct())
        {
            var (text, encoding) = FileSystem.ReadText(file);
            var updated = text;
            foreach (var (oldName, newName) in ordered)
            {
                updated = ReplaceProjectName(updated, oldName, newName);
            }

            if (updated != text)
            {
                FileSystem.WriteText(file, updated, encoding);
                context.Verbose($"updated references in {context.Relative(file)}");
            }
        }

        context.RefreshProjects();
    }

    /// <summary>Replaces a project name only where it is a whole path segment / project name, never inside a longer name.</summary>
    internal static string ReplaceProjectName(string text, string oldName, string newName)
    {
        foreach (var (before, after) in new[]
                 {
                     ($"{oldName}\\", $"{newName}\\"), ($"{oldName}/", $"{newName}/"), ($"{oldName}.csproj", $"{newName}.csproj"),
                     ($"\"{oldName}\"", $"\"{newName}\""), ($">{oldName}<", $">{newName}<"), ($"{oldName}.dll", $"{newName}.dll")
                 })
        {
            text = text.Replace(before, after, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>
    /// An IDE that has the output solution open restores it while the output is replaced and recreates bin/obj of the
    /// renamed projects (e.g. Acme.Domain/obj), which would stop the folder rename: a folder with nothing else is removed.
    /// </summary>
    internal static void DeleteBuildLeftovers(string directory)
    {
        if (Directory.Exists(directory) &&
            Directory.EnumerateFileSystemEntries(directory).All(e => Path.GetFileName(e) is "bin" or "obj" && Directory.Exists(e)))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
