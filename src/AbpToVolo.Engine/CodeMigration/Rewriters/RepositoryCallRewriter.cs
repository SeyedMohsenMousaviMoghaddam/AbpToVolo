using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.CodeMigration.Rewriters;

/// <summary>
/// Repository API differences. The rewriter works on syntax only, so a receiver is treated as a repository
/// when its name contains "Repository" (Repository, _personRepository, ...) or ends with "Repo".
/// </summary>
public sealed class RepositoryCallRewriter : MigrationRewriter
{
    /// <summary>Same arguments, new method name.</summary>
    private static readonly Dictionary<string, string> Renames = new(StringComparer.Ordinal)
    {
        ["GetAllListAsync"] = "GetListAsync",
        ["FirstOrDefaultAsync"] = "FindAsync",
        ["SingleAsync"] = "GetAsync",
    };

    private static readonly HashSet<string> RemovedSyncMethods = new(StringComparer.Ordinal)
    {
        "Get", "FirstOrDefault", "Single", "GetAllList", "Insert", "InsertAndGetId", "InsertOrUpdate",
        "InsertOrUpdateAndGetId", "Update", "Delete", "Count", "LongCount", "Load", "Query"
    };

    public RepositoryCallRewriter(FileMigrationContext context) : base(context)
    {
    }

    public static bool IsRepository(ExpressionSyntax receiver)
    {
        var name = receiver switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            _ => null
        };

        return name is not null &&
               (name.Contains("Repository", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Repo", StringComparison.OrdinalIgnoreCase));
    }

    public override SyntaxNode? VisitAwaitExpression(AwaitExpressionSyntax node)
    {
        var visited = (AwaitExpressionSyntax)base.VisitAwaitExpression(node)!;
        if (visited.Expression is InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "InsertAndGetIdAsync" } access
            } invocation)
        {
            // await repo.InsertAndGetIdAsync(e)  →  (await repo.InsertAsync(e, autoSave: true)).Id
            // (only Boilerplate repositories have InsertAndGetIdAsync, whatever the field is called: _peopleRepo, _peopleReop…)
            Context.Change("InsertAndGetIdAsync(e) → (await InsertAsync(e, autoSave: true)).Id");
            var arguments = ParseArgumentList(
                "(" + string.Join(", ", invocation.ArgumentList.Arguments.Select(a => a.ToString()).Append("autoSave: true")) + ")");
            var insert = invocation.WithExpression(access.WithName(IdentifierName("InsertAsync"))).WithArgumentList(arguments);
            if (node.Parent is ExpressionStatementSyntax)
            {
                // The id is not used: "await repo.InsertAsync(entity, autoSave: true);"
                return visited.WithExpression(insert);
            }

            return MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxHelpers.AwaitParenthesized(insert), IdentifierName("Id"))
                .WithTriviaFrom(visited);
        }

        return visited;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        if (visited.Expression is not MemberAccessExpressionSyntax access || !IsRepository(access.Expression))
        {
            return visited;
        }

        var method = access.Name.Identifier.Text;
        var argumentCount = visited.ArgumentList.Arguments.Count;
        var isAsyncContext = SyntaxHelpers.IsInsideAsyncFunction(node);

        switch (method)
        {
            case "GetAll" when argumentCount == 0:
            case "GetAllIncluding":
            {
                var newName = method == "GetAll" ? "GetQueryableAsync" : "WithDetailsAsync";
                if (!isAsyncContext)
                {
                    Todo($"{method}() became {newName}() in ABP.io: make this method async and use (await repository.{newName}()).");
                    return visited;
                }

                Context.Change($"Repository.{method}() → (await {newName}())");
                var call = visited.WithExpression(access.WithName(IdentifierName(newName)));
                return SyntaxHelpers.AwaitParenthesized(call).WithTriviaFrom(visited);
            }

            case "CountAsync" or "LongCountAsync" when argumentCount == 0:
                Context.Change($"Repository.{method}() → GetCountAsync()");
                return visited.WithExpression(access.WithName(IdentifierName("GetCountAsync")));

            case "CountAsync" or "LongCountAsync" when isAsyncContext:
            {
                // repo.CountAsync(predicate)  →  AsyncExecuter.CountAsync((await repo.GetQueryableAsync()).Where(predicate))
                Context.Change($"Repository.{method}(predicate) → AsyncExecuter.{method}(query.Where(predicate))");
                Context.RequireUsing("System.Linq");
                var queryable = SyntaxHelpers.AwaitParenthesized(
                    InvocationExpression(access.WithName(IdentifierName("GetQueryableAsync"))));
                var where = InvocationExpression(
                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, queryable, IdentifierName("Where")),
                    visited.ArgumentList.WithoutTrivia());
                Todo("AsyncExecuter is available in ApplicationService/DomainService; inject IAsyncQueryableExecuter elsewhere.");
                return InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("AsyncExecuter"), IdentifierName(method)),
                        ArgumentList(SingletonSeparatedList(Argument(where))))
                    .WithTriviaFrom(visited);
            }

            case "InsertAndGetIdAsync":
                // Awaited calls are handled by VisitAwaitExpression.
                if (node.Parent is not AwaitExpressionSyntax)
                {
                    Todo("InsertAndGetIdAsync does not exist in ABP.io: use (await repository.InsertAsync(entity, autoSave: true)).Id.");
                }

                return visited;

            case "InsertOrUpdateAsync" or "InsertOrUpdateAndGetIdAsync":
                Todo($"{method} does not exist in ABP.io: check whether the entity is new and call InsertAsync or UpdateAsync.");
                return visited;

            case "GetDbContext":
                Todo("Inside EfCoreRepository use 'await GetDbContextAsync()' / 'await GetDbSetAsync()'.");
                return visited;
        }

        if (Renames.TryGetValue(method, out var renamed))
        {
            Context.Change($"Repository.{method} → {renamed}");
            return visited.WithExpression(access.WithName(IdentifierName(renamed)));
        }

        if (RemovedSyncMethods.Contains(method))
        {
            Todo($"Synchronous repository method '{method}' does not exist in ABP.io; use {method}Async (or the async equivalent).");
        }

        return visited;
    }
}
