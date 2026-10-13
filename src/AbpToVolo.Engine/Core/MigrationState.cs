using System.Text.Json;
using System.Text.Json.Serialization;

namespace AbpToVolo.Core;

/// <summary>
/// What a conversion decided once and must decide the same way on every later conversion into the same output folder
/// (.abptovolo/state.json, committed with the converted code). This makes re-converting a newer version of the
/// source deterministic, so the "converted" branch of the git workflow only changes where the source changed:
///   - SquashedUpTo: the last migration squashed by the first conversion. Databases deployed from the converted
///     solution know only this id, so later source migrations are never squashed into it (they are reported instead:
///     the ABP.io schema change is created with "dotnet ef migrations add" in the working branch).
///   - MigrationIds: ids of the generated migrations (RenameModuleZeroTables, ConvertModuleZeroKeys).
///   - StringEncryptionPassPhrase: the generated passphrase (changing it breaks the encrypted data).
/// </summary>
public sealed class MigrationState
{
    public const string Folder = ".abptovolo";
    public const string FileName = "state.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string? ToolVersion { get; set; }

    public string? SquashedUpTo { get; set; }

    public Dictionary<string, string> MigrationIds { get; set; } = new(StringComparer.Ordinal);

    public string? StringEncryptionPassPhrase { get; set; }

    /// <summary>Source revision of the last conversion (git HEAD of the input folder, when it is a git repository).</summary>
    public string? SourceRevision { get; set; }

    [JsonIgnore]
    public bool IsUpdate { get; private set; }

    public static string PathIn(string outputRoot) => System.IO.Path.Combine(outputRoot, Folder, FileName);

    /// <summary>The folder name used before the tool was renamed AbpToVolo (read and moved to <see cref="Folder"/>).</summary>
    public const string LegacyFolder = ".abpmigrator";

    public static MigrationState Load(string outputRoot)
    {
        var path = PathIn(outputRoot);
        var legacy = System.IO.Path.Combine(outputRoot, LegacyFolder);
        if (!File.Exists(path) && File.Exists(System.IO.Path.Combine(legacy, FileName)))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.Move(System.IO.Path.Combine(legacy, FileName), path);
            if (!Directory.EnumerateFileSystemEntries(legacy).Any()) Directory.Delete(legacy);
        }

        if (!File.Exists(path)) return new MigrationState();

        var state = JsonSerializer.Deserialize<MigrationState>(File.ReadAllText(path), Json) ?? new MigrationState();
        state.IsUpdate = true;
        return state;
    }

    public void Save(string outputRoot)
    {
        var path = PathIn(outputRoot);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json) + Environment.NewLine);
    }
}
