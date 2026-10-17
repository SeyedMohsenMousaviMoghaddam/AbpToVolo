using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.CodeMigration.Rewriters;

/// <summary>
/// Boilerplate's UserFriendlyException constructors are (message, details) and (int code, message[, details]); ABP.io's are
/// (message, code, details, innerException, ...). Without names the details would become the error code (and the
/// clients would not show them):
///   new UserFriendlyException(message, details)          → new UserFriendlyException(message, details: details)
///   new UserFriendlyException(404, message[, details])   → new UserFriendlyException(message, code: "404"[, details: details])
/// (message, exception) is left to the compiler-guided fixes (innerException:), which know the argument types.
/// </summary>
public sealed partial class ExceptionRewriter : MigrationRewriter
{
    public ExceptionRewriter(FileMigrationContext context) : base(context)
    {
    }

    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var visited = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node)!;
        if (SyntaxHelpers.SimpleName(visited.Type) != "UserFriendlyException" || visited.ArgumentList is not { Arguments: var args } list ||
            args.Count is < 2 or > 3 || args.Any(a => a.NameColon is not null))
        {
            return visited;
        }

        // (int code, message[, details])
        if (args[0].Expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } code)
        {
            var reordered = new List<ArgumentSyntax>
            {
                args[1].WithLeadingTrivia(args[0].GetLeadingTrivia()),
                Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(code.Token.ValueText))).WithNameColon(SyntaxHelpers.NamedArgument("code"))
            };
            if (args.Count == 3 && !LooksLikeException(args[2].Expression)) reordered.Add(args[2].WithNameColon(SyntaxHelpers.NamedArgument("details")));
            else if (args.Count == 3) return visited;
            Context.Change("UserFriendlyException(code, message, ...) → (message, code: \"...\", ...)");
            return visited.WithArgumentList(list.WithArguments(SeparatedList(reordered, Enumerable.Repeat(Token(SyntaxKind.CommaToken).WithTrailingTrivia(Space), reordered.Count - 1))));
        }

        // (message, details) — not (message, exception)
        if (args.Count == 2 && !LooksLikeException(args[1].Expression))
        {
            Context.Change("UserFriendlyException(message, details) → (message, details: details)");
            return visited.WithArgumentList(list.WithArguments(args.Replace(args[1], args[1].WithNameColon(SyntaxHelpers.NamedArgument("details")))));
        }

        return visited;
    }

    /// <summary>ex, e, exception, innerException, someException, new XException(...), x.InnerException.</summary>
    private static bool LooksLikeException(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax id => ExceptionName().IsMatch(id.Identifier.Text),
        MemberAccessExpressionSyntax member => ExceptionName().IsMatch(member.Name.Identifier.Text),
        ObjectCreationExpressionSyntax creation => SyntaxHelpers.SimpleName(creation.Type).EndsWith("Exception", StringComparison.Ordinal),
        _ => false
    };

    [GeneratedRegex(@"^(e|ex|exc|err|exception|\w*Exception|\w*exception)$")]
    private static partial Regex ExceptionName();
}
