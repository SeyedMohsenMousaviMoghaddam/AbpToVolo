using System.Text.Json;
using System.Text.Json.Serialization;

namespace AbpToVolo.Core;

/// <summary>
/// Editable, serializable conversion settings (source/destination folders and options). Used by the console
/// (--config / --save-config) and by the desktop app (remembered between runs).
/// </summary>
public sealed class MigrationSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string SourcePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public string AbpVersion { get; set; } = "10.7.0";
    public string TargetFramework { get; set; } = "net10.0";
    public string? MicrosoftPackagesVersion { get; set; }
    public string? SolutionName { get; set; }

    /// <summary>ABP.io layer and module names (Core → Domain ...). On by default.</summary>
    public bool UseAbpLayerNames { get; set; } = true;
    public bool ConvertModuleZero { get; set; } = true;
    public bool StandardizeHost { get; set; } = true;
    public bool CreateContracts { get; set; } = true;
    public bool CreateTests { get; set; } = true;
    public MigrationsMode Migrations { get; set; } = MigrationsMode.Squash;
    public bool CompilerFixes { get; set; } = true;
    public bool StubUnresolved { get; set; }
    public bool Build { get; set; }
    public bool Force { get; set; }
    public bool Verbose { get; set; }

    public MigrationOptions ToOptions() => new()
    {
        InputPath = Path.GetFullPath(SourcePath),
        OutputPath = Path.GetFullPath(DestinationPath),
        AbpVersion = AbpVersion,
        TargetFramework = TargetFramework,
        MicrosoftPackagesVersion = string.IsNullOrWhiteSpace(MicrosoftPackagesVersion) ? null : MicrosoftPackagesVersion,
        SolutionName = string.IsNullOrWhiteSpace(SolutionName) ? null : SolutionName,
        RenameLayers = UseAbpLayerNames,
        ConvertModuleZero = ConvertModuleZero,
        StandardizeHost = StandardizeHost,
        CreateContracts = CreateContracts,
        CreateTests = CreateTests,
        Migrations = Migrations,
        CompilerFixes = CompilerFixes,
        StubUnresolved = StubUnresolved,
        Build = Build,
        Force = Force,
        Verbose = Verbose
    };

    /// <summary>Returns the problems that prevent a run (empty when the settings are valid).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(SourcePath)) errors.Add("The source folder is required.");
        else if (!Directory.Exists(SourcePath)) errors.Add($"The source folder does not exist: {SourcePath}");
        else if (!FileSystem.EnumerateFiles(SourcePath, "*.csproj").Any()) errors.Add("The source folder contains no .csproj file.");

        if (string.IsNullOrWhiteSpace(DestinationPath))
        {
            errors.Add("The destination folder is required.");
        }
        else if (!string.IsNullOrWhiteSpace(SourcePath))
        {
            var source = Path.GetFullPath(SourcePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var destination = Path.GetFullPath(DestinationPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (destination.StartsWith(source, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("The destination folder must not be the source folder or inside it.");
            }
        }

        return errors;
    }

    public static MigrationSettings Load(string path) =>
        JsonSerializer.Deserialize<MigrationSettings>(File.ReadAllText(path), JsonOptions) ?? new MigrationSettings();

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
