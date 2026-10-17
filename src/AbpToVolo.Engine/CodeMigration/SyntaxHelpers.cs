using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.CodeMigration;

public static class SyntaxHelpers
{
    public const string TodoPrefix = "TODO [AbpToVolo]: ";

    /// <summary>"name: " (named argument, with the usual space after the colon).</summary>
    public static NameColonSyntax NamedArgument(string name) => NameColon(IdentifierName(name), Token(SyntaxKind.ColonToken).WithTrailingTrivia(Space));

    /// <summary>
    /// new { x.A } → new { Name = value }, keeping the layout: the line break / indentation in front of the member moves to
    /// the name, and " = " separates it from the value.
    /// </summary>
    public static AnonymousObjectMemberDeclaratorSyntax NamedMember(AnonymousObjectMemberDeclaratorSyntax declarator, string name, ExpressionSyntax value) =>
        AnonymousObjectMemberDeclarator(
            NameEquals(IdentifierName(name).WithLeadingTrivia(declarator.GetLeadingTrivia()), Token(SyntaxKind.EqualsToken).WithLeadingTrivia(Space).WithTrailingTrivia(Space)),
            value.WithoutLeadingTrivia().WithTrailingTrivia(declarator.GetTrailingTrivia()));

    /// <summary>Name of a type without namespace qualification and generic arguments: Abp.Foo.Bar&lt;T&gt; → Bar.</summary>
    public static string SimpleName(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax q => SimpleName(q.Right),
        AliasQualifiedNameSyntax a => SimpleName(a.Name),
        GenericNameSyntax g => g.Identifier.Text,
        IdentifierNameSyntax i => i.Identifier.Text,
        _ => type.ToString()
    };

    public static bool HasBaseType(TypeDeclarationSyntax type, params string[] names) =>
        type.BaseList?.Types.Any(t => names.Contains(SimpleName(t.Type), StringComparer.Ordinal)) == true;

    public static BaseTypeSyntax? FindBaseType(TypeDeclarationSyntax type, params string[] names) =>
        type.BaseList?.Types.FirstOrDefault(t => names.Contains(SimpleName(t.Type), StringComparer.Ordinal));

    public static bool IsInsideAsyncFunction(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax m:
                    return m.Modifiers.Any(SyntaxKind.AsyncKeyword);
                case LocalFunctionStatementSyntax l:
                    return l.Modifiers.Any(SyntaxKind.AsyncKeyword);
                case AnonymousFunctionExpressionSyntax f:
                    return f.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword);
                case AccessorDeclarationSyntax or ConstructorDeclarationSyntax or BaseTypeDeclarationSyntax:
                    return false;
            }
        }

        return false;
    }

    public static ClassDeclarationSyntax? EnclosingClass(SyntaxNode node) =>
        node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();

    /// <summary>Whitespace directly in front of the node's first token (its indentation).</summary>
    public static string Indentation(SyntaxNode node)
    {
        var leading = node.GetLeadingTrivia();
        return leading.Count > 0 && leading[^1].IsKind(SyntaxKind.WhitespaceTrivia) ? leading[^1].ToString() : string.Empty;
    }

    /// <summary>Adds "// TODO [AbpToVolo]: ..." lines above the node, using the node's indentation.</summary>
    public static TNode WithTodos<TNode>(TNode node, IEnumerable<string> messages, string newLine) where TNode : SyntaxNode
    {
        var indent = Indentation(node);
        var trivia = node.GetLeadingTrivia().ToList();
        if (trivia.Count > 0 && trivia[^1].IsKind(SyntaxKind.WhitespaceTrivia))
        {
            trivia.RemoveAt(trivia.Count - 1);
        }

        foreach (var message in messages.Distinct())
        {
            trivia.Add(Whitespace(indent));
            trivia.Add(Comment("// " + TodoPrefix + message));
            trivia.Add(EndOfLine(newLine));
        }

        trivia.Add(Whitespace(indent));
        return node.WithLeadingTrivia(trivia);
    }

    /// <summary>Comment lines (with indentation) that describe a statement that was removed.</summary>
    public static IEnumerable<SyntaxTrivia> CommentLines(string indent, string newLine, IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            yield return Whitespace(indent);
            yield return Comment("// " + line);
            yield return EndOfLine(newLine);
        }
    }

    /// <summary>The source of a statement turned into commented-out lines (keeps the original for the developer).</summary>
    public static IEnumerable<string> CommentOutLines(SyntaxNode node, string reason)
    {
        yield return TodoPrefix + reason;
        var text = node.WithoutTrivia().ToFullString();
        foreach (var line in text.Split('\n'))
        {
            yield return line.TrimEnd('\r').TrimStart();
        }
    }

    /// <summary>expr → (await expr)</summary>
    public static ExpressionSyntax AwaitParenthesized(ExpressionSyntax expression) =>
        ParenthesizedExpression(AwaitExpression(Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(Space), expression.WithoutTrivia()));

    public static ExpressionSyntax Expr(string code) => ParseExpression(code);

    public static StatementSyntax Stmt(string code) => ParseStatement(code);

    public static string Text(SyntaxNode node) => node.WithoutTrivia().ToString();
}
