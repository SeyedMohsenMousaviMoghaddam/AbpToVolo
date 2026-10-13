using AbpToVolo.Mappings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AbpToVolo.CodeMigration.Rewriters;

/// <summary>
/// Finds Module Zero types (users, roles, tenants, managers...). These have no 1:1 translation: in ABP.io
/// they are provided by the Identity / TenantManagement / PermissionManagement modules.
/// </summary>
public sealed class ZeroDetector : CSharpSyntaxWalker
{
    private static readonly string[] ZeroMarkers = ["AddAbpIdentity", "AbpZeroTemplate", "AbpBootstrapper", "IocManager.Instance"];

    private readonly FileMigrationContext _context;

    public ZeroDetector(FileMigrationContext context)
    {
        _context = context;
    }

    public override void VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        if (node.BaseList is not null)
        {
            foreach (var baseType in node.BaseList.Types)
            {
                var name = SyntaxHelpers.SimpleName(baseType.Type);
                if (name == "AbpZeroDbContext")
                {
                    // Converted by DbContextRewriter; only the identity tables need attention.
                    _context.UsesZero = true;
                    continue;
                }

                if (TypeMappings.ZeroTypes.TryGetValue(name, out var replacement))
                {
                    _context.UsesZero = true;
                    _context.FileTodo($"'{node.Identifier.Text}' derives from Module Zero type '{name}'. " +
                                      $"Replace it with {replacement}.");
                }
            }
        }

        base.VisitClassDeclaration(node);
    }

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var text = node.Expression.ToString();
        foreach (var marker in ZeroMarkers)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                _context.FileTodo($"'{marker}' is Boilerplate infrastructure with no direct ABP.io equivalent (see report).");
            }
        }

        base.VisitInvocationExpression(node);
    }
}
