using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AbpToVolo.CodeMigration;

/// <summary>
/// Base class for all conversion rewriters. Gives every rewriter a way to attach a TODO comment to the
/// statement / member that contains the node currently being rewritten.
/// Note: inside Visit* overrides, ancestor lookups must use the original node (the parameter), because
/// nodes returned from base.Visit* are detached from the tree.
/// </summary>
public abstract class MigrationRewriter : CSharpSyntaxRewriter
{
    private readonly Stack<List<string>> _todoScopes = new();

    protected MigrationRewriter(FileMigrationContext context) : base(visitIntoStructuredTrivia: false)
    {
        Context = context;
    }

    protected FileMigrationContext Context { get; }

    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        if (!IsTodoAnchor(node))
        {
            return base.Visit(node);
        }

        _todoScopes.Push([]);
        var result = base.Visit(node);
        var todos = _todoScopes.Pop();
        return result is not null && todos.Count > 0
            ? SyntaxHelpers.WithTodos(result, todos, Context.NewLine)
            : result;
    }

    /// <summary>Adds a TODO comment above the statement (or member) being rewritten.</summary>
    protected void Todo(string message)
    {
        if (_todoScopes.Count > 0)
        {
            _todoScopes.Peek().Add(message);
        }
        else
        {
            Context.FileTodo(message);
        }
    }

    private static bool IsTodoAnchor(SyntaxNode? node) => node is
        (StatementSyntax and not BlockSyntax) or
        BaseFieldDeclarationSyntax or
        BasePropertyDeclarationSyntax or
        BaseMethodDeclarationSyntax;

    protected static bool IsIdentifier(ExpressionSyntax expression, string name) =>
        expression is IdentifierNameSyntax id && id.Identifier.Text == name;

    protected static SyntaxToken Id(string name, SyntaxToken triviaSource) =>
        SyntaxFactory.Identifier(triviaSource.LeadingTrivia, name, triviaSource.TrailingTrivia);
}
