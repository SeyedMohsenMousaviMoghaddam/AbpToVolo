using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.CodeMigration;

/// <summary>Adds typeof(X) to the [DependsOn] attribute of an ABP module class.</summary>
public static class ModuleDependencyEditor
{
    /// <returns>The updated source, or null when the file has no matching module class.</returns>
    public static string? AddDependsOn(string source, string moduleType, string? moduleNamespace, Func<string, bool> isTargetModule)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var module = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => SyntaxHelpers.HasBaseType(c, "AbpModule") && isTargetModule(c.Identifier.Text));
        if (module is null)
        {
            return null;
        }

        var newLine = source.Contains("\r\n") ? "\r\n" : "\n";
        var dependsOn = module.AttributeLists.SelectMany(l => l.Attributes)
            .FirstOrDefault(a => SyntaxHelpers.SimpleName(a.Name) is "DependsOn" or "DependsOnAttribute");

        CompilationUnitSyntax updated;
        if (dependsOn?.ArgumentList is { } args)
        {
            if (args.Arguments.Any(a => a.Expression is TypeOfExpressionSyntax t && SyntaxHelpers.SimpleName(t.Type) == moduleType))
            {
                return source;
            }

            var multiLine = args.ToFullString().Contains('\n');
            var indent = args.Arguments.Count > 0 ? SyntaxHelpers.Indentation(args.Arguments[0]) : "    ";
            var argument = AttributeArgument(TypeOfExpression(ParseTypeName(moduleType)))
                .WithLeadingTrivia(multiLine ? TriviaList(Whitespace(indent)) : TriviaList(Space));
            var separator = Token(SyntaxKind.CommaToken).WithTrailingTrivia(multiLine ? TriviaList(EndOfLine(newLine)) : TriviaList());
            var list = args.Arguments.Count == 0
                ? SingletonSeparatedList(argument.WithoutLeadingTrivia())
                : args.Arguments.GetWithSeparators().Add(separator).Add(argument) is var nodes ? SeparatedList<AttributeArgumentSyntax>(nodes) : default;
            updated = root.ReplaceNode(args, args.WithArguments(list));
        }
        else
        {
            var indent = SyntaxHelpers.Indentation(module);
            var attribute = AttributeList(SingletonSeparatedList(Attribute(IdentifierName("DependsOn"),
                    ParseAttributeArgumentList($"(typeof({moduleType}))"))))
                .WithLeadingTrivia(module.GetLeadingTrivia())
                .WithTrailingTrivia(EndOfLine(newLine));
            updated = root.ReplaceNode(module, module.WithLeadingTrivia(Whitespace(indent)).WithAttributeLists(module.AttributeLists.Insert(0, attribute)));
        }

        var fileNamespace = updated.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault() is { } ns ? SolutionModel.FullNamespace(ns) : "";
        var needsUsing = moduleNamespace is not null &&
                         fileNamespace != moduleNamespace && !fileNamespace.StartsWith(moduleNamespace + ".", StringComparison.Ordinal) &&
                         !updated.Usings.Any(u => u.Name?.ToString() == moduleNamespace);
        if (needsUsing)
        {
            updated = updated.AddUsings(UsingDirective(ParseName(moduleNamespace!)).NormalizeWhitespace().WithTrailingTrivia(EndOfLine(newLine)));
        }

        return updated.ToFullString();
    }
}
