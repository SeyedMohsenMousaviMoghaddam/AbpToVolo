using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.CodeMigration.Rewriters;

/// <summary>
/// AsyncCrudAppService members that were renamed in ABP.io's CrudAppService:
/// GetAllAsync → GetListAsync, CreateFilteredQuery → CreateFilteredQueryAsync, *PermissionName → *PolicyName ...
/// </summary>
public sealed class CrudAppServiceRewriter : MigrationRewriter
{
    private static readonly Dictionary<string, string> MemberRenames = new(StringComparer.Ordinal)
    {
        ["GetAllAsync"] = "GetListAsync",
        ["GetAllPermissionName"] = "GetListPolicyName",
        ["GetPermissionName"] = "GetPolicyName",
        ["CreatePermissionName"] = "CreatePolicyName",
        ["UpdatePermissionName"] = "UpdatePolicyName",
        ["DeletePermissionName"] = "DeletePolicyName",
        ["MapToEntityDto"] = "MapToGetOutputDto",
    };

    private int _depth;

    public CrudAppServiceRewriter(FileMigrationContext context) : base(context)
    {
    }

    private static bool IsCrudService(TypeDeclarationSyntax type) =>
        SyntaxHelpers.HasBaseType(type, "AsyncCrudAppService", "CrudAppService", "IAsyncCrudAppService");

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) => VisitType(node, n => base.VisitClassDeclaration(n));

    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => VisitType(node, n => base.VisitInterfaceDeclaration(n));

    private SyntaxNode? VisitType<T>(T node, Func<T, SyntaxNode?> visitBase) where T : TypeDeclarationSyntax
    {
        if (!IsCrudService(node))
        {
            return visitBase(node);
        }

        _depth++;
        var result = visitBase(node);
        _depth--;
        return result;
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        if (_depth == 0)
        {
            return visited;
        }

        var name = visited.Identifier.Text;
        if (MemberRenames.TryGetValue(name, out var newName))
        {
            Context.Change($"CrudAppService: {name} → {newName}");
            if (name == "MapToEntityDto")
            {
                Todo("ABP.io calls MapToGetOutputDto for Get and MapToGetListOutputDto for GetList; override both if needed.");
            }

            return visited.WithIdentifier(Id(newName, visited.Identifier));
        }

        if (name == "CreateFilteredQuery" && !visited.Modifiers.Any(SyntaxKind.AsyncKeyword))
        {
            Context.RequireUsing("System.Threading.Tasks");
            Context.Change("CrudAppService: CreateFilteredQuery → async CreateFilteredQueryAsync");
            var returnType = ParseTypeName($"Task<{SyntaxHelpers.Text(visited.ReturnType)}>").WithTriviaFrom(visited.ReturnType);
            return visited
                .WithIdentifier(Id("CreateFilteredQueryAsync", visited.Identifier))
                .WithReturnType(returnType)
                .WithModifiers(visited.Modifiers.Add(Token(SyntaxKind.AsyncKeyword).WithTrailingTrivia(Space)));
        }

        if (name is "CheckGetAllPermission" or "CheckGetPermission" or "CheckCreatePermission" or "CheckUpdatePermission" or "CheckDeletePermission")
        {
            Todo($"{name}() became Check*PolicyAsync() (async, returns Task) in ABP.io.");
        }

        return visited;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        if (_depth == 0 || visited.Expression is not IdentifierNameSyntax { Identifier.Text: "CreateFilteredQuery" })
        {
            return visited;
        }

        if (!SyntaxHelpers.IsInsideAsyncFunction(node))
        {
            Todo("CreateFilteredQuery is CreateFilteredQueryAsync in ABP.io; call it with await from an async method.");
            return visited;
        }

        var call = visited.WithExpression(IdentifierName("CreateFilteredQueryAsync"));
        return SyntaxHelpers.AwaitParenthesized(call).WithTriviaFrom(visited);
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        if (_depth > 0 && MemberRenames.TryGetValue(node.Identifier.Text, out var newName) &&
            node.Parent is not (QualifiedNameSyntax or UsingDirectiveSyntax))
        {
            return node.WithIdentifier(Id(newName, node.Identifier));
        }

        return base.VisitIdentifierName(node);
    }
}
