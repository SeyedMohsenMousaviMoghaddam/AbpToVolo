using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AbpToVolo.Core;

/// <summary>
/// A full Roslyn compilation of one converted project, with the real references (NuGet compile assets from
/// obj/project.assets.json, the built project references, the shared frameworks), so fixes can use the semantic model
/// of ABP.io types (e.g. "is this x.Id the Id of a Tenant, inside an EF Core query?"). Needs a restored project whose
/// project references are built (true in the compiler-guided fixes once the build reaches the project).
/// </summary>
public sealed class SemanticProject
{
    public required CSharpCompilation Compilation { get; init; }

    /// <summary>File path → syntax tree of the project's own sources.</summary>
    public required IReadOnlyDictionary<string, SyntaxTree> Trees { get; init; }

    public static SemanticProject? Load(string csproj, string configuration = "Debug")
    {
        var directory = Path.GetDirectoryName(csproj)!;
        var assets = Path.Combine(directory, "obj", "project.assets.json");
        if (!File.Exists(assets)) return null;

        using var json = JsonDocument.Parse(File.ReadAllText(assets));
        var root = json.RootElement;
        if (!root.TryGetProperty("targets", out var targets) || targets.EnumerateObject().FirstOrDefault() is not { Value.ValueKind: JsonValueKind.Object } target) return null;
        var tfm = target.Name.Split('/')[0];
        var packageFolders = root.TryGetProperty("packageFolders", out var folders) ? folders.EnumerateObject().Select(f => f.Name).ToList() : [];
        var libraries = root.GetProperty("libraries");

        var references = new List<string>();
        foreach (var library in target.Value.EnumerateObject())
        {
            var type = library.Value.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "project")
            {
                var name = library.Name.Split('/')[0];
                if (libraries.TryGetProperty(library.Name, out var info) && info.TryGetProperty("path", out var path))
                {
                    var referenced = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(directory, path.GetString()!)))!;
                    var dll = Path.Combine(referenced, "bin", configuration, tfm, name + ".dll");
                    if (File.Exists(dll)) references.Add(dll);
                }

                continue;
            }

            if (type != "package" || !library.Value.TryGetProperty("compile", out var compile)) continue;
            if (!libraries.TryGetProperty(library.Name, out var package) || !package.TryGetProperty("path", out var packagePath)) continue;
            foreach (var asset in compile.EnumerateObject().Select(a => a.Name).Where(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                var file = packageFolders.Select(f => Path.Combine(f, packagePath.GetString()!, asset)).FirstOrDefault(File.Exists);
                if (file is not null) references.Add(file);
            }
        }

        // Shared frameworks (the running .NET and ASP.NET Core of the same major version).
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        references.AddRange(Directory.EnumerateFiles(runtime, "*.dll").Where(IsManaged));
        var shared = Path.GetDirectoryName(Path.GetDirectoryName(runtime))!;
        var aspNet = Path.Combine(shared, "Microsoft.AspNetCore.App");
        if (Directory.Exists(aspNet))
        {
            var version = Path.GetFileName(runtime);
            var folder = Directory.EnumerateDirectories(aspNet).OrderByDescending(d => d == Path.Combine(aspNet, version)).ThenByDescending(d => d).FirstOrDefault();
            if (folder is not null) references.AddRange(Directory.EnumerateFiles(folder, "*.dll").Where(IsManaged));
        }

        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var trees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        var sources = FileSystem.EnumerateFiles(directory, "*.cs")
            .Concat(Directory.Exists(Path.Combine(directory, "obj", configuration, tfm))
                ? Directory.EnumerateFiles(Path.Combine(directory, "obj", configuration, tfm), "*.cs")
                : []);
        foreach (var file in sources.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            trees[file] = CSharpSyntaxTree.ParseText(File.ReadAllText(file), parse, path: file);
        }

        var compilation = CSharpCompilation.Create(Path.GetFileNameWithoutExtension(csproj), trees.Values,
            references.Distinct(StringComparer.OrdinalIgnoreCase).Select(r => MetadataReference.CreateFromFile(r)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        return new SemanticProject { Compilation = compilation, Trees = trees };
    }

    private static bool IsManaged(string dll)
    {
        try
        {
            using var stream = File.OpenRead(dll);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            return pe.HasMetadata;
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
