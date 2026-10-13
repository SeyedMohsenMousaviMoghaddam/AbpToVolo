using AbpToVolo.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AbpToVolo.CodeMigration;

/// <summary>
/// When code moves out of the compilation (legacy folder), a namespace can disappear; "using" directives of such
/// namespaces would no longer compile and are removed.
/// </summary>
public static class UsingCleaner
{
    public static int RemoveVanishedNamespaces(MigrationContext context, IReadOnlySet<string> namespacesBefore)
    {
        var files = context.Projects.SelectMany(p => FileSystem.EnumerateFiles(p.Directory, "*.cs"))
            .Where(f => !context.IsExcluded(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var roots = files.ToDictionary(f => f, f => CSharpSyntaxTree.ParseText(File.ReadAllText(f)).GetCompilationUnitRoot());
        var remaining = roots.Values
            .SelectMany(r => r.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
            .SelectMany(ns => Prefixes(SolutionModel.FullNamespace(ns)))
            .ToHashSet(StringComparer.Ordinal);
        var vanished = namespacesBefore.Where(n => !remaining.Contains(n)).ToHashSet(StringComparer.Ordinal);
        if (vanished.Count == 0) return 0;

        var changed = 0;
        foreach (var (file, root) in roots)
        {
            var remove = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Where(u => u.Alias is null && u.Name is not null && vanished.Contains(u.Name.ToString()))
                .ToList();
            if (remove.Count == 0) continue;

            var updated = root.RemoveNodes(remove, SyntaxRemoveOptions.KeepNoTrivia)!;
            FileSystem.WriteText(file, updated.ToFullString(), FileSystem.ReadText(file).Encoding);
            changed++;
        }

        return changed;
    }

    /// <summary>"A.B.C" declares the namespaces A, A.B and A.B.C.</summary>
    private static IEnumerable<string> Prefixes(string ns)
    {
        var parts = ns.Split('.');
        for (var i = 1; i <= parts.Length; i++) yield return string.Join(".", parts.Take(i));
    }
}
