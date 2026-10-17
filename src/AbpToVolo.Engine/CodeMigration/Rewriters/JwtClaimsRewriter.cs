using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.CodeMigration.Rewriters;

/// <summary>
/// The TokenAuth login of the Boilerplate template (CreateJwtClaims) adds "sub" to the claims of the login identity. ABP.io's
/// claims principal factory already puts the user id in "sub" (AbpClaimTypes.UserId), so the JWT would carry
/// "sub": [id, id], which no token handler accepts (IDX11020: every request with that token is anonymous → 401):
///   claims.AddRange(new[] { new Claim(JwtRegisteredClaimNames.Sub, ...), ... })
///   → claims.RemoveAll(c => c.Type == JwtRegisteredClaimNames.Sub); claims.AddRange(...)
/// </summary>
public sealed class JwtClaimsRewriter : MigrationRewriter
{
    public JwtClaimsRewriter(FileMigrationContext context) : base(context)
    {
    }

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node) =>
        node.ToString().Contains("JwtRegisteredClaimNames.Sub", StringComparison.Ordinal) ? base.VisitCompilationUnit(node) : node;

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var visited = (BlockSyntax)base.VisitBlock(node)!;
        var statements = new List<StatementSyntax>();
        foreach (var statement in visited.Statements)
        {
            if (AddsSubject(statement) is { } list && !visited.Statements.Any(s => s.ToString().StartsWith($"{list}.RemoveAll(", StringComparison.Ordinal)))
            {
                statements.Add(ParseStatement($"{list}.RemoveAll(c => c.Type == JwtRegisteredClaimNames.Sub);")
                    .WithLeadingTrivia(Whitespace(SyntaxHelpers.Indentation(statement)))
                    .WithTrailingTrivia(EndOfLine(Context.NewLine)));
                Context.Change("Claims list: existing \"sub\" removed before JwtRegisteredClaimNames.Sub is added (ABP.io identities already have it)");
            }

            statements.Add(statement);
        }

        return statements.Count == visited.Statements.Count ? visited : visited.WithStatements(List(statements));
    }

    /// <summary>list.Add(new Claim(JwtRegisteredClaimNames.Sub, ...)) or list.AddRange(... new Claim(JwtRegisteredClaimNames.Sub, ...) ...) → "list".</summary>
    private static string? AddsSubject(StatementSyntax statement)
    {
        if (statement is not ExpressionStatementSyntax
            {
                Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Add" or "AddRange", Expression: IdentifierNameSyntax list } } invocation
            })
        {
            return null;
        }

        return invocation.ArgumentList.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Any(c =>
            SyntaxHelpers.SimpleName(c.Type) == "Claim" &&
            c.ArgumentList?.Arguments.FirstOrDefault()?.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.Text: "Sub", Expression: var owner
            } && owner.ToString().EndsWith("JwtRegisteredClaimNames", StringComparison.Ordinal))
            ? list.Identifier.Text
            : null;
    }
}
