using System.Text.Json;
using System.Text.RegularExpressions;
using AbpToVolo.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AbpToVolo.Steps;

/// <summary>
/// The HTTP endpoints of the source (ASP.NET Boilerplate conventions) compared with the swagger.json of the converted
/// host: every route the front end called must still exist. Syntax only (the source is not built):
///   - application services (classes named *AppService / *ApplicationService): public methods →
///     /api/services/app/{Service}/{Action}, verb from [Http*] or the method-name prefix (Get → GET, Update → PUT ...);
///   - controllers (*Controller): [Route]/[Http*("...")] templates with [controller]/[action] replaced.
/// [RemoteService(false)], [NonAction], static/abstract members are skipped. "Async" is removed from action names
/// (ASP.NET Core default, as in the source).
/// </summary>
public static partial class EndpointParity
{
    public sealed record Endpoint(string Verb, string Path, string Source);

    public sealed record Result(int Expected, List<Endpoint> Missing, List<(Endpoint Source, string ActualVerb)> VerbChanged);

    private static readonly string[] Verbs = ["GET", "POST", "PUT", "DELETE", "PATCH"];

    public static List<Endpoint> SourceEndpoints(string sourceRoot)
    {
        var endpoints = new List<Endpoint>();
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');
            if (Regex.IsMatch(relative, @"(^|/)(bin|obj|node_modules|test|tests)/", RegexOptions.IgnoreCase) ||
                relative.Contains(".Tests/", StringComparison.OrdinalIgnoreCase) || relative.Contains("/Migrations/", StringComparison.Ordinal))
            {
                continue;
            }

            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
            foreach (var type in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (type.Modifiers.Any(SyntaxKind.AbstractKeyword) || type.Modifiers.Any(SyntaxKind.StaticKeyword) || !type.Modifiers.Any(SyntaxKind.PublicKeyword)) continue;
                if (type.TypeParameterList is not null || IsRemoteServiceDisabled(type.AttributeLists)) continue;
                var name = type.Identifier.Text;
                var isService = name.EndsWith("AppService", StringComparison.Ordinal) || name.EndsWith("ApplicationService", StringComparison.Ordinal);
                var isController = !isService && name.EndsWith("Controller", StringComparison.Ordinal);
                if (!isService && !isController) continue;

                var service = isService ? RemovePostfix(name, "ApplicationService", "AppService") : RemovePostfix(name, "Controller");
                var classRoute = RouteTemplate(type.AttributeLists);
                foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
                {
                    if (!method.Modifiers.Any(SyntaxKind.PublicKeyword) || method.Modifiers.Any(SyntaxKind.StaticKeyword) || method.TypeParameterList is not null ||
                        method.ExplicitInterfaceSpecifier is not null || HasAttribute(method.AttributeLists, "NonAction") || IsRemoteServiceDisabled(method.AttributeLists))
                    {
                        continue;
                    }

                    var action = RemovePostfix(method.Identifier.Text, "Async");
                    var (verb, verbTemplate) = HttpAttribute(method.AttributeLists);
                    var methodRoute = RouteTemplate(method.AttributeLists) ?? verbTemplate;
                    string path;
                    if (isService && methodRoute is null && classRoute is null)
                    {
                        path = $"/api/services/app/{service}/{action}";
                        verb ??= ConventionalVerb(action);
                    }
                    else if (methodRoute is not null || classRoute is not null)
                    {
                        var template = methodRoute is not null && (methodRoute.StartsWith('/') || methodRoute.StartsWith("~/", StringComparison.Ordinal) || classRoute is null)
                            ? methodRoute.TrimStart('~')
                            : $"{classRoute}/{methodRoute}".TrimEnd('/');
                        path = "/" + template.Replace("[controller]", service, StringComparison.OrdinalIgnoreCase)
                            .Replace("[action]", action, StringComparison.OrdinalIgnoreCase).Trim('/');
                        if (isService) verb ??= ConventionalVerb(action);
                    }
                    else
                    {
                        continue; // conventional MVC route ({controller}/{action}): not in Swagger
                    }

                    endpoints.Add(new Endpoint(verb ?? "", path, $"{relative}: {name}.{method.Identifier.Text}"));
                }
            }
        }

        return endpoints.DistinctBy(e => (e.Verb, Normalize(e.Path))).ToList();
    }

    /// <summary>(verb, normalized path) of swagger.json.</summary>
    public static List<(string Verb, string Path)> SwaggerEndpoints(string swaggerJson)
    {
        var list = new List<(string, string)>();
        using var document = JsonDocument.Parse(swaggerJson);
        if (!document.RootElement.TryGetProperty("paths", out var paths)) return list;
        foreach (var path in paths.EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (Verbs.Contains(operation.Name.ToUpperInvariant())) list.Add((operation.Name.ToUpperInvariant(), path.Name));
            }
        }

        return list;
    }

    public static Result Compare(List<Endpoint> source, List<(string Verb, string Path)> swagger)
    {
        var byPath = swagger.GroupBy(s => Normalize(s.Path)).ToDictionary(g => g.Key, g => g.Select(s => s.Verb).ToHashSet(), StringComparer.OrdinalIgnoreCase);
        var missing = new List<Endpoint>();
        var verbChanged = new List<(Endpoint, string)>();
        foreach (var endpoint in source)
        {
            if (!byPath.TryGetValue(Normalize(endpoint.Path), out var verbs)) missing.Add(endpoint);
            else if (endpoint.Verb.Length > 0 && !verbs.Contains(endpoint.Verb)) verbChanged.Add((endpoint, string.Join("/", verbs)));
        }

        return new Result(source.Count, missing, verbChanged);
    }

    /// <summary>Lower case, route parameters as {} (names and constraints do not matter for the client).</summary>
    private static string Normalize(string path) => RouteParameter().Replace(path.Trim().TrimEnd('/'), "{}").ToLowerInvariant();

    private static string RemovePostfix(string name, params string[] postfixes)
    {
        foreach (var postfix in postfixes)
        {
            if (name.Length > postfix.Length && name.EndsWith(postfix, StringComparison.Ordinal)) return name[..^postfix.Length];
        }

        return name;
    }

    /// <summary>Boilerplate/ABP.io method-name convention.</summary>
    private static string ConventionalVerb(string action) => action switch
    {
        _ when action.StartsWith("Get", StringComparison.OrdinalIgnoreCase) => "GET",
        _ when action.StartsWith("Put", StringComparison.OrdinalIgnoreCase) || action.StartsWith("Update", StringComparison.OrdinalIgnoreCase) => "PUT",
        _ when action.StartsWith("Delete", StringComparison.OrdinalIgnoreCase) || action.StartsWith("Remove", StringComparison.OrdinalIgnoreCase) => "DELETE",
        _ when action.StartsWith("Patch", StringComparison.OrdinalIgnoreCase) => "PATCH",
        _ => "POST"
    };

    private static IEnumerable<AttributeSyntax> Attributes(SyntaxList<AttributeListSyntax> lists) => lists.SelectMany(l => l.Attributes);

    private static string AttributeName(AttributeSyntax attribute)
    {
        var name = attribute.Name switch { QualifiedNameSyntax q => q.Right.Identifier.Text, SimpleNameSyntax s => s.Identifier.Text, var n => n.ToString() };
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    private static bool HasAttribute(SyntaxList<AttributeListSyntax> lists, string name) => Attributes(lists).Any(a => AttributeName(a) == name);

    private static bool IsRemoteServiceDisabled(SyntaxList<AttributeListSyntax> lists) =>
        Attributes(lists).Any(a => AttributeName(a) == "RemoteService" && a.ArgumentList?.Arguments.Any(x => x.Expression.ToString() == "false") == true);

    private static string? FirstString(AttributeSyntax attribute) =>
        attribute.ArgumentList?.Arguments.Where(a => a.NameEquals is null && a.NameColon is null)
            .Select(a => (a.Expression as LiteralExpressionSyntax)?.Token.ValueText).FirstOrDefault();

    private static string? RouteTemplate(SyntaxList<AttributeListSyntax> lists) =>
        Attributes(lists).Where(a => AttributeName(a) == "Route").Select(FirstString).FirstOrDefault(t => t is not null);

    private static (string? Verb, string? Template) HttpAttribute(SyntaxList<AttributeListSyntax> lists)
    {
        foreach (var attribute in Attributes(lists))
        {
            var name = AttributeName(attribute);
            if (name.StartsWith("Http", StringComparison.Ordinal) && Verbs.Contains(name[4..].ToUpperInvariant()))
            {
                return (name[4..].ToUpperInvariant(), FirstString(attribute));
            }
        }

        return (null, null);
    }

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex RouteParameter();
}
