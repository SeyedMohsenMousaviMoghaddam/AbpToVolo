using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.CodeMigration.Rewriters;

/// <summary>
/// EF Core 10 translates string.Join over a distinct sequence inside a query to STRING_AGG(DISTINCT ...), which SQL Server
/// does not accept ("Incorrect syntax near ','"); EF Core 8 fetched the values and joined them in memory. ToHashSet()
/// fetches the values again (AsEnumerable() is dropped by EF) and keeps Distinct's result: same comparer, first-seen order:
///   string.Join(", ", g.Select(x => x.Name).Distinct())
///   → string.Join(", ", g.Select(x => x.Name).ToHashSet())
/// </summary>
public sealed class StringJoinDistinctRewriter : MigrationRewriter
{
    public StringJoinDistinctRewriter(FileMigrationContext context) : base(context)
    {
    }

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node) =>
        node.ToString().Contains(".Distinct()", StringComparison.Ordinal) ? base.VisitCompilationUnit(node) : node;

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        if (visited.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Join", Expression: var owner } ||
            owner.ToString() is not ("string" or "String" or "System.String") ||
            visited.ArgumentList.Arguments.Count != 2 ||
            visited.ArgumentList.Arguments[1].Expression is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Distinct" }, ArgumentList.Arguments.Count: 0
            } distinct)
        {
            return visited;
        }

        Context.Change("string.Join(..., Distinct()) → ToHashSet() (EF Core 10 would send STRING_AGG(DISTINCT), invalid on SQL Server)");
        var toHashSet = distinct.WithExpression(((MemberAccessExpressionSyntax)distinct.Expression).WithName(IdentifierName("ToHashSet")));
        return visited.WithArgumentList(visited.ArgumentList.WithArguments(
            visited.ArgumentList.Arguments.Replace(visited.ArgumentList.Arguments[1], visited.ArgumentList.Arguments[1].WithExpression(toHashSet))));
    }
}
