using System.Text.RegularExpressions;
using AbpToVolo.CodeMigration;
using AbpToVolo.Core;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AbpToVolo.Steps;

/// <summary>
/// Gives the module classes their ABP.io template names, matching the ABP.io project names:
///   XCoreModule → XDomainModule, XWebCoreModule → XHttpApiModule, XWebHostModule → XHttpApiHostModule,
///   XEntityFrameworkModule → XEntityFrameworkCoreModule, XMigratorModule → XDbMigratorModule.
/// Every reference in the solution is updated (whole-word rename) and the module files are renamed.
/// </summary>
public sealed class StandardNamesStep : IMigrationStep
{
    private static readonly (ProjectLayer Layer, string[] OldSuffixes, string NewSuffix)[] Rules =
    [
        (ProjectLayer.Domain, ["CoreModule"], "DomainModule"),
        (ProjectLayer.HttpApi, ["WebCoreModule"], "HttpApiModule"),
        (ProjectLayer.Host, ["WebHostModule"], "HttpApiHostModule"),
        (ProjectLayer.EntityFrameworkCore, ["EntityFrameworkModule"], "EntityFrameworkCoreModule"),
        (ProjectLayer.DbMigrator, ["MigratorModule"], "DbMigratorModule"),
    ];

    public string Name => "ABP.io module names";

    public bool IsEnabled(MigrationOptions options) => options.RenameLayers;

    public void Execute(MigrationContext context)
    {
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (layer, oldSuffixes, newSuffix) in Rules)
        {
            foreach (var project in context.Projects.Where(p => p.Layer == layer))
            {
                foreach (var module in ModuleClasses(project))
                {
                    var suffix = oldSuffixes.FirstOrDefault(s => module.EndsWith(s, StringComparison.Ordinal) && !module.EndsWith(newSuffix, StringComparison.Ordinal));
                    if (suffix is null) continue;
                    var prefix = module[..^suffix.Length];
                    if (layer == ProjectLayer.DbMigrator && prefix.EndsWith("Db", StringComparison.Ordinal)) continue;
                    renames[module] = prefix + newSuffix;
                }
            }
        }

        if (renames.Count == 0)
        {
            context.Report.Info(Name, "Module classes already follow the ABP.io names.");
            return;
        }

        var pattern = new Regex(@"\b(" + string.Join("|", renames.Keys.Select(Regex.Escape)) + @")\b");
        var files = context.Projects.SelectMany(p => FileSystem.EnumerateFiles(p.Directory, "*.cs")).Where(f => !context.IsExcluded(f)).Distinct().ToList();
        foreach (var file in files)
        {
            var (text, encoding) = FileSystem.ReadText(file);
            var updated = pattern.Replace(text, m => renames[m.Value]);
            var target = file;
            if (renames.TryGetValue(Path.GetFileNameWithoutExtension(file), out var newFileName))
            {
                target = Path.Combine(Path.GetDirectoryName(file)!, newFileName + ".cs");
            }

            if (updated == text && target == file) continue;
            FileSystem.WriteText(target, updated, encoding);
            if (target != file) File.Delete(file);
        }

        foreach (var (oldName, newName) in renames)
        {
            context.Report.Change(Name, $"{oldName} → {newName}");
        }

        context.Log.WriteLine($"{renames.Count} module classes renamed to ABP.io names");
    }

    private static IEnumerable<string> ModuleClasses(ProjectInfo project) =>
        FileSystem.EnumerateFiles(project.Directory, "*.cs")
            .Where(f => File.ReadAllText(f).Contains("AbpModule", StringComparison.Ordinal))
            .SelectMany(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f)).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            .Where(c => SyntaxHelpers.HasBaseType(c, "AbpModule"))
            .Select(c => c.Identifier.Text);
}
