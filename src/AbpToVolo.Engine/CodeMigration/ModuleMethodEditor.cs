using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.CodeMigration;

/// <summary>Small source edits on ABP module classes and files: add a statement to a lifecycle method, add usings.</summary>
public static class ModuleMethodEditor
{
    /// <summary>Adds <paramref name="statement"/> at the top of the module's method (created when missing).</summary>
    public static string? AddStatement(string source, Func<string, bool> isTargetModule, string methodName, string parameter, string statement)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var module = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => SyntaxHelpers.HasBaseType(c, "AbpModule") && isTargetModule(c.Identifier.Text));
        if (module is null) return null;
        if (module.ToFullString().Contains(statement.Trim(), StringComparison.Ordinal)) return source;

        var nl = source.Contains("\r\n") ? "\r\n" : "\n";
        var memberIndent = module.Members.Count > 0 ? SyntaxHelpers.Indentation(module.Members[0]) : SyntaxHelpers.Indentation(module) + "    ";
        var bodyIndent = memberIndent + "    ";
        var method = module.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName && m.Body is not null);

        ClassDeclarationSyntax updated;
        if (method is not null)
        {
            var newStatement = ParseStatement(statement).WithLeadingTrivia(Whitespace(bodyIndent)).WithTrailingTrivia(EndOfLine(nl));
            updated = module.ReplaceNode(method, method.WithBody(method.Body!.WithStatements(method.Body.Statements.Insert(0, newStatement))));
        }
        else
        {
            var code = $"{memberIndent}public override void {methodName}({parameter}){nl}{memberIndent}{{{nl}{bodyIndent}{statement}{nl}{memberIndent}}}{nl}";
            var member = ParseMemberDeclaration(code)!.WithLeadingTrivia(EndOfLine(nl), Whitespace(memberIndent));
            updated = module.WithMembers(module.Members.Insert(0, member));
        }

        return root.ReplaceNode(module, updated).ToFullString();
    }

    public static string AddUsings(string source, IEnumerable<string> namespaces)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var nl = source.Contains("\r\n") ? "\r\n" : "\n";
        var existing = root.DescendantNodes().OfType<UsingDirectiveSyntax>().Select(u => u.Name?.ToString()).ToHashSet(StringComparer.Ordinal);
        var missing = namespaces.Where(n => !string.IsNullOrEmpty(n) && !existing.Contains(n)).Distinct().ToList();
        if (missing.Count == 0) return source;

        var hadUsings = root.Usings.Count > 0;
        var directives = missing.Select(n => UsingDirective(ParseName(n)).NormalizeWhitespace().WithTrailingTrivia(EndOfLine(nl))).ToList();
        if (!hadUsings) directives[^1] = directives[^1].WithTrailingTrivia(EndOfLine(nl), EndOfLine(nl));
        return root.WithUsings(root.Usings.AddRange(directives)).ToFullString();
    }
}
