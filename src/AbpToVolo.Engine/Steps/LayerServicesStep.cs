using System.Text.RegularExpressions;
using AbpToVolo.CodeMigration;
using AbpToVolo.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AbpToVolo.Steps;

/// <summary>
/// Framework services the code of a layer injects but nobody registers any more. In Boilerplate they came from the
/// web host (Startup.ConfigureServices, Abp.AspNetCore); in ABP.io every module registers what its layer needs, so
/// the layer also works in the generated tests and in the DbMigrator:
///   IHttpClientFactory / HttpClient → context.Services.AddHttpClient()
///   IHubContext&lt;THub&gt;              → context.Services.AddSignalR()
/// </summary>
public sealed partial class LayerServicesStep : IMigrationStep
{
    public string Name => "Layer services (HttpClient, SignalR)";

    private sealed record Service(string Name, Regex Usage, string Statement, string Namespace);

    private static readonly Service[] Services =
    [
        new("IHttpClientFactory", HttpClientUsage(), "context.Services.AddHttpClient();", "Microsoft.Extensions.DependencyInjection"),
        new("IHubContext<THub>", HubContextUsage(), "context.Services.AddSignalR();", "Microsoft.Extensions.DependencyInjection"),
        new("IHttpContextAccessor", HttpContextAccessorUsage(), "context.Services.AddHttpContextAccessor();", "Microsoft.Extensions.DependencyInjection"),
    ];

    public void Execute(MigrationContext context)
    {
        ExposeMismatchedInterfaces(context);
        RegisterLayerServices(context);
    }

    private static readonly string[] ServiceSuffixes = ["AppService", "Service", "Manager", "Repository", "Provider", "Store", "Checker", "Builder", "Client"];

    /// <summary>
    /// ABP.io exposes a class only under the interfaces its name ends with (MessageAppService is not exposed as
    /// IMessagesAppService); Castle in Boilerplate was more lenient. Such classes get
    /// [ExposeServices(typeof(IX), IncludeDefaults = true, IncludeSelf = true)] so injecting the interface keeps working.
    /// </summary>
    private void ExposeMismatchedInterfaces(MigrationContext context)
    {
        var files = context.Projects.Where(p => p.Layer is not (ProjectLayer.Tests or ProjectLayer.Unknown))
            .SelectMany(p => FileSystem.EnumerateFiles(p.Directory, "*.cs"))
            .Where(f => !context.IsExcluded(f))
            .ToList();
        var trees = files.ToDictionary(f => f, f => CSharpSyntaxTree.ParseText(File.ReadAllText(f)).GetCompilationUnitRoot());
        var interfaces = trees.Values.SelectMany(r => r.DescendantNodes().OfType<InterfaceDeclarationSyntax>())
            .Select(i => i.Identifier.Text)
            .Where(n => ServiceSuffixes.Any(s => n.EndsWith(s, StringComparison.Ordinal)))
            .ToHashSet(StringComparer.Ordinal);

        var count = 0;
        foreach (var (file, root) in trees)
        {
            var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(c => c.BaseList is not null && !c.Modifiers.Any(m => m.Text is "abstract" or "static") &&
                            !c.AttributeLists.SelectMany(a => a.Attributes).Any(a => SyntaxHelpers.SimpleName(a.Name) is "ExposeServices" or "ExposeServicesAttribute" or "Dependency" or "DependencyAttribute"))
                .Select(c => (Class: c, Missing: c.BaseList!.Types.Select(t => SyntaxHelpers.SimpleName(t.Type))
                    .Where(n => interfaces.Contains(n) && n.Length > 1 && !c.Identifier.Text.EndsWith(n[1..], StringComparison.Ordinal))
                    .ToList()))
                .Where(x => x.Missing.Count > 0)
                .ToList();
            if (classes.Count == 0) continue;

            var updated = root.ReplaceNodes(classes.Select(x => x.Class), (original, rewritten) =>
            {
                var missing = classes.First(x => x.Class == original).Missing;
                var attribute = SyntaxFactory.ParseCompilationUnit(
                        $"[ExposeServices({string.Join(", ", missing.Select(m => $"typeof({m})"))}, IncludeDefaults = true, IncludeSelf = true)] class X {{ }}")
                    .DescendantNodes().OfType<AttributeListSyntax>().First();
                var indent = SyntaxHelpers.Indentation(rewritten);
                var newline = root.ToFullString().Contains("\r\n") ? "\r\n" : "\n";
                var leading = rewritten.GetLeadingTrivia();
                return rewritten
                    .WithLeadingTrivia(SyntaxFactory.Whitespace(indent))
                    .WithAttributeLists(rewritten.AttributeLists.Insert(0, attribute
                        .WithLeadingTrivia(SyntaxFactory.Whitespace(indent))
                        .WithTrailingTrivia(SyntaxFactory.EndOfLine(newline))))
                    .WithLeadingTrivia(leading);
            });

            FileSystem.WriteText(file, ModuleMethodEditor.AddUsings(updated.ToFullString(), ["Volo.Abp.DependencyInjection"]), FileSystem.ReadText(file).Encoding);
            count += classes.Count;
            foreach (var (cls, missing) in classes)
            {
                context.Report.Change(Name, $"{cls.Identifier.Text}: [ExposeServices({string.Join(", ", missing)})] (ABP.io exposes only interfaces matching the class name)", context.Relative(file));
            }
        }

        if (count > 0) context.Log.WriteLine($"{count} classes exposed under interfaces that do not match their name");
    }

    private void RegisterLayerServices(MigrationContext context)
    {
        foreach (var project in context.Projects.Where(p => p.Layer is ProjectLayer.Domain or ProjectLayer.Application or ProjectLayer.EntityFrameworkCore))
        {
            var code = string.Join('\n', FileSystem.EnumerateFiles(project.Directory, "*.cs").Where(f => !context.IsExcluded(f)).Select(File.ReadAllText));
            var moduleFile = AbpModulesStep.FindModuleFile(project, AbpModulesStep.ModuleNameFilters[project.Layer]);

            foreach (var service in Services.Where(s => s.Usage.IsMatch(code)))
            {
                if (moduleFile is null)
                {
                    context.Report.Todo(Name, $"{project.Name} injects {service.Name}: call {service.Statement} in its module.");
                    continue;
                }

                var text = File.ReadAllText(moduleFile);
                var updated = ModuleMethodEditor.AddStatement(text, AbpModulesStep.ModuleNameFilters[project.Layer], "ConfigureServices", "ServiceConfigurationContext context", service.Statement);
                if (updated is null || updated == text) continue;

                FileSystem.WriteText(moduleFile, ModuleMethodEditor.AddUsings(updated, [service.Namespace]));
                context.Report.Change(Name, $"{project.Name} injects {service.Name}: its module calls {service.Statement}", context.Relative(moduleFile));
            }
        }
    }

    [GeneratedRegex(@"\bIHttpClientFactory\b|[(,]\s*HttpClient\s+\w+\s*[,)]")]
    private static partial Regex HttpClientUsage();

    [GeneratedRegex(@"\bIHubContext\s*<")]
    private static partial Regex HubContextUsage();

    [GeneratedRegex(@"\bIHttpContextAccessor\b")]
    private static partial Regex HttpContextAccessorUsage();
}
