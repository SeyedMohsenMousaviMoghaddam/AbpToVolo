using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.CodeMigration.Rewriters;

/// <summary>
/// Custom repositories of the Boilerplate template (stored procedures, raw SQL) get the active transaction through
/// Abp.Data.IActiveTransactionProvider. ABP.io has no such service: the repository reaches it through its DbContext.
///   ctor(IDbContextProvider&lt;T&gt; p, IActiveTransactionProvider t) : base(p, t)   → ctor(IDbContextProvider&lt;T&gt; p) : base(p)
///   await _transactionProvider.GetActiveTransactionAsync(args)              → (await GetDbContextAsync()).Database.CurrentTransaction?.GetDbTransaction()
///   await _transactionProvider.GetActiveConnectionAsync(args)               → (await GetDbContextAsync()).Database.GetDbConnection()
/// Fields/properties of that type and their assignments are removed. Without this, every custom repository (and so every
/// service that injects one) stops compiling, and the services cannot be resolved.
/// </summary>
public sealed class ActiveTransactionRewriter : MigrationRewriter
{
    private const string ProviderType = "IActiveTransactionProvider";
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);

    public ActiveTransactionRewriter(FileMigrationContext context) : base(context)
    {
    }

    private static bool IsProvider(TypeSyntax? type) => type is not null && SyntaxHelpers.SimpleName(type) == ProviderType;

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        if (!node.ToString().Contains(ProviderType, StringComparison.Ordinal)) return node;

        // Names of the parameters, fields and properties that held the provider.
        foreach (var parameter in node.DescendantNodes().OfType<ParameterSyntax>().Where(p => IsProvider(p.Type)))
        {
            _removed.Add(parameter.Identifier.Text);
        }

        foreach (var property in node.DescendantNodes().OfType<PropertyDeclarationSyntax>().Where(p => IsProvider(p.Type)))
        {
            _removed.Add(property.Identifier.Text);
        }

        foreach (var field in node.DescendantNodes().OfType<FieldDeclarationSyntax>().Where(f => IsProvider(f.Declaration.Type)))
        {
            foreach (var variable in field.Declaration.Variables) _removed.Add(variable.Identifier.Text);
        }

        return base.VisitCompilationUnit(node);
    }

    public override SyntaxNode? VisitParameterList(ParameterListSyntax node)
    {
        var visited = (ParameterListSyntax)base.VisitParameterList(node)!;
        var kept = visited.Parameters.Where(p => !IsProvider(p.Type)).ToList();
        if (kept.Count == visited.Parameters.Count) return visited;

        Context.Change("Removed the IActiveTransactionProvider parameter (ABP.io repositories use their DbContext)");
        return visited.WithParameters(SeparatedList(kept.Select((p, i) => i == 0 ? p.WithLeadingTrivia(visited.Parameters[0].GetLeadingTrivia()) : p)));
    }

    public override SyntaxNode? VisitConstructorInitializer(ConstructorInitializerSyntax node)
    {
        var visited = (ConstructorInitializerSyntax)base.VisitConstructorInitializer(node)!;
        var kept = visited.ArgumentList.Arguments.Where(a => !(a.Expression is IdentifierNameSyntax id && _removed.Contains(id.Identifier.Text))).ToList();
        return kept.Count == visited.ArgumentList.Arguments.Count ? visited : visited.WithArgumentList(visited.ArgumentList.WithArguments(SeparatedList(kept)));
    }

    public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node) =>
        IsProvider(node.Declaration.Type) ? null : base.VisitFieldDeclaration(node);

    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node) =>
        IsProvider(node.Type) ? null : base.VisitPropertyDeclaration(node);

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        // _transactionProvider = transactionProvider;
        if (node.Expression is AssignmentExpressionSyntax { Right: IdentifierNameSyntax right } && _removed.Contains(right.Identifier.Text))
        {
            return null;
        }

        return base.VisitExpressionStatement(node);
    }

    public override SyntaxNode? VisitAwaitExpression(AwaitExpressionSyntax node)
    {
        if (Replacement(node.Expression, awaited: true) is { } replacement) return replacement.WithTriviaFrom(node);
        return base.VisitAwaitExpression(node);
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (Replacement(node, awaited: false) is { } replacement) return replacement.WithTriviaFrom(node);
        return base.VisitInvocationExpression(node);
    }

    private ExpressionSyntax? Replacement(ExpressionSyntax expression, bool awaited)  // awaited: the await is replaced too
    {
        if (expression is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } ||
            access.Expression is not (IdentifierNameSyntax or MemberAccessExpressionSyntax) ||
            !_removed.Contains(SyntaxHelpers.Text(access.Expression).Split('.').Last()))
        {
            return null;
        }

        var member = access.Name.Identifier.Text switch
        {
            "GetActiveTransactionAsync" or "GetActiveTransaction" => "Database.CurrentTransaction?.GetDbTransaction()",
            "GetActiveConnectionAsync" or "GetActiveConnection" => "Database.GetDbConnection()",
            _ => null
        };
        if (member is null) return null;

        var isAsync = SyntaxHelpers.IsInsideAsyncFunction(expression);
        var dbContext = isAsync ? "(await GetDbContextAsync())" : "GetDbContextAsync().GetAwaiter().GetResult()";
        Context.Change($"IActiveTransactionProvider.{access.Name.Identifier.Text} → DbContext.{member.Split('?')[0]}");
        Context.RequireUsing("Microsoft.EntityFrameworkCore");
        Context.RequireUsing("Microsoft.EntityFrameworkCore.Storage");
        var result = ParseExpression($"{dbContext}.{member}");
        return ParenthesizedExpression(result);
    }
}
