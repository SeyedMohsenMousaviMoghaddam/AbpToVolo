using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.CodeMigration.Rewriters;

/// <summary>
/// Host bootstrapping:
/// services.AddAbp&lt;T&gt;(...) / AddAbpWithoutCreatingServiceProvider&lt;T&gt;(...) → services.AddApplication&lt;T&gt;(),
/// app.UseAbp(...) → app.InitializeApplication(), .UseCastleWindsor(...) → .UseAutofac().
/// </summary>
public sealed class StartupRewriter : MigrationRewriter
{
    private bool _returnedServiceProvider;

    public StartupRewriter(FileMigrationContext context) : base(context)
    {
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var previous = _returnedServiceProvider;
        _returnedServiceProvider = false;
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;

        if (_returnedServiceProvider && SyntaxHelpers.Text(visited.ReturnType) == "IServiceProvider")
        {
            Context.Change($"{visited.Identifier.Text}: return type IServiceProvider → void");
            visited = visited.WithReturnType(PredefinedType(Token(SyntaxKind.VoidKeyword)).WithTriviaFrom(visited.ReturnType));
        }

        _returnedServiceProvider = previous;
        return visited;
    }

    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
        var visited = (ReturnStatementSyntax)base.VisitReturnStatement(node)!;
        if (visited.Expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "AddApplication" } } invocation)
        {
            _returnedServiceProvider = true;
            return ExpressionStatement(invocation.WithoutTrivia()).WithTriviaFrom(visited);
        }

        return visited;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        if (visited.Expression is not MemberAccessExpressionSyntax access)
        {
            return visited;
        }

        switch (access.Name)
        {
            case GenericNameSyntax { Identifier.Text: "AddAbp" or "AddAbpWithoutCreatingServiceProvider" } generic:
                Context.Change($"services.{generic.Identifier.Text}<T>() → services.AddApplication<T>()");
                Context.RequireUsing("Microsoft.Extensions.DependencyInjection");
                if (visited.ArgumentList.Arguments.Count > 0)
                {
                    Todo("AddAbp options (Castle Windsor / log4net facilities) were dropped; configure logging with Serilog or Microsoft.Extensions.Logging.");
                }

                return visited
                    .WithExpression(access.WithName(GenericName(Identifier("AddApplication"), generic.TypeArgumentList)))
                    .WithArgumentList(ArgumentList().WithTriviaFrom(visited.ArgumentList));

            case IdentifierNameSyntax { Identifier.Text: "UseAbp" }:
                Context.Change("app.UseAbp() → app.InitializeApplication()");
                Todo("app.InitializeApplication() runs every module's OnApplicationInitialization. Consider moving this middleware pipeline into the host module.");
                return visited
                    .WithExpression(access.WithName(IdentifierName("InitializeApplication")))
                    .WithArgumentList(ArgumentList().WithTriviaFrom(visited.ArgumentList));

            case IdentifierNameSyntax { Identifier.Text: "UseCastleWindsor" }:
                Context.Change(".UseCastleWindsor(...) → .UseAutofac()");
                Context.RequireUsing("Microsoft.Extensions.Hosting");
                return visited
                    .WithExpression(access.WithName(IdentifierName("UseAutofac")))
                    .WithArgumentList(ArgumentList().WithTriviaFrom(visited.ArgumentList));

            case GenericNameSyntax { Identifier.Text: "MapHub" } hub when hub.TypeArgumentList.Arguments.ToString() == "AbpCommonHub":
                Todo("AbpCommonHub does not exist in ABP.io; remove this hub mapping (ABP.io maps hubs deriving from AbpHub automatically).");
                return visited;
        }

        return visited;
    }

    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        if (SyntaxHelpers.SimpleName(node.Type) == "AbpAutoValidateAntiforgeryTokenAttribute")
        {
            Todo("ABP.io validates anti-forgery tokens through AbpAntiForgeryOptions; remove this filter.");
        }

        return base.VisitObjectCreationExpression(node);
    }
}
