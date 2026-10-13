using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AbpToVolo.CodeMigration;

/// <summary>
/// One Roslyn compilation over all source files of the solution. Only the .NET base library is referenced, so
/// NuGet types (Volo.Abp, EF Core...) are unresolved — that is fine: the model is used to find which
/// solution-declared types a piece of code depends on.
/// </summary>
public sealed class SolutionModel
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview, DocumentationMode.None);
    private static readonly Lazy<MetadataReference[]> FrameworkReferences = new(LoadFrameworkReferences);

    private readonly Dictionary<SyntaxTree, SemanticModel> _models = new();

    private SolutionModel(CSharpCompilation compilation, Dictionary<SyntaxTree, string> files)
    {
        Compilation = compilation;
        FileOf = files;
        TreeOf = files.ToDictionary(f => f.Value, f => f.Key, StringComparer.OrdinalIgnoreCase);
        Namespaces = compilation.SyntaxTrees
            .SelectMany(t => t.GetRoot().DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
            .Select(FullNamespace)
            .ToHashSet(StringComparer.Ordinal);
    }

    public CSharpCompilation Compilation { get; }
    public IReadOnlyDictionary<SyntaxTree, string> FileOf { get; }
    public IReadOnlyDictionary<string, SyntaxTree> TreeOf { get; }

    /// <summary>Every namespace declared in the solution's source.</summary>
    public IReadOnlySet<string> Namespaces { get; }

    public static SolutionModel Build(IEnumerable<string> files)
    {
        var map = new Dictionary<SyntaxTree, string>();
        foreach (var file in files)
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), ParseOptions, file);
            map[tree] = file;
        }

        var compilation = CSharpCompilation.Create("AbpToVoloSolutionModel", map.Keys, FrameworkReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable));
        return new SolutionModel(compilation, map);
    }

    public SemanticModel Model(SyntaxTree tree)
    {
        if (!_models.TryGetValue(tree, out var model))
        {
            model = Compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            _models[tree] = model;
        }

        return model;
    }

    public static string FullNamespace(BaseNamespaceDeclarationSyntax ns)
    {
        var parts = new List<string> { ns.Name.ToString() };
        for (var parent = ns.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is BaseNamespaceDeclarationSyntax outer) parts.Insert(0, outer.Name.ToString());
        }

        return string.Join(".", parts);
    }

    private static MetadataReference[] LoadFrameworkReferences()
    {
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "";
        return tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p =>
            {
                var name = Path.GetFileName(p);
                return name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
                       name is "System.dll" or "mscorlib.dll" or "netstandard.dll";
            })
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();
    }
}
