using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.CodeMigration.Rewriters;

/// <summary>
/// AuthorizationProvider.SetPermissions → PermissionDefinitionProvider.Define (with a permission group) and
/// SettingProvider.GetSettingDefinitions → SettingDefinitionProvider.Define.
/// </summary>
public sealed class DefinitionProviderRewriter : MigrationRewriter
{
    private string? _permissionContext;

    public DefinitionProviderRewriter(FileMigrationContext context) : base(context)
    {
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        if (node.Parent is not ClassDeclarationSyntax cls || node.ParameterList.Parameters.Count != 1 || node.Body is null)
        {
            return base.VisitMethodDeclaration(node);
        }

        if (node.Identifier.Text == "SetPermissions" && SyntaxHelpers.HasBaseType(cls, "AuthorizationProvider"))
        {
            return ConvertPermissions(node, cls);
        }

        if (node.Identifier.Text == "GetSettingDefinitions" && SyntaxHelpers.HasBaseType(cls, "SettingProvider"))
        {
            return ConvertSettings(node) ?? base.VisitMethodDeclaration(node);
        }

        return base.VisitMethodDeclaration(node);
    }

    private SyntaxNode ConvertPermissions(MethodDeclarationSyntax node, ClassDeclarationSyntax cls)
    {
        _permissionContext = node.ParameterList.Parameters[0].Identifier.Text;
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        _permissionContext = null;

        var groupName = cls.Identifier.Text.Replace("AuthorizationProvider", "", StringComparison.Ordinal);
        if (groupName.Length == 0) groupName = "App";

        var body = visited.Body!;
        var indent = body.Statements.Count > 0 ? SyntaxHelpers.Indentation(body.Statements[0]) : SyntaxHelpers.Indentation(visited) + "    ";
        var groupStatement = SyntaxHelpers.Stmt($"var group = {visited.ParameterList.Parameters[0].Identifier.Text}.AddGroup(\"{groupName}\");")
            .WithLeadingTrivia(Whitespace(indent))
            .WithTrailingTrivia(EndOfLine(Context.NewLine), EndOfLine(Context.NewLine));

        Context.RequireUsing("Volo.Abp.Authorization.Permissions");
        Context.Change($"SetPermissions → Define with permission group \"{groupName}\"");
        return visited
            .WithIdentifier(Id("Define", visited.Identifier))
            .WithBody(body.WithStatements(body.Statements.Insert(0, groupStatement)));
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        if (_permissionContext is null || visited.Expression is not MemberAccessExpressionSyntax access)
        {
            return visited;
        }

        var method = access.Name.Identifier.Text;
        if (method == "CreatePermission" && IsIdentifier(access.Expression, _permissionContext))
        {
            access = access.WithExpression(IdentifierName("group").WithTriviaFrom(access.Expression)).WithName(IdentifierName("AddPermission"));
        }
        else if (method == "CreateChildPermission")
        {
            access = access.WithName(IdentifierName("AddChild"));
        }
        else
        {
            return visited;
        }

        return visited.WithExpression(access).WithArgumentList(ConvertPermissionArguments(visited.ArgumentList));
    }

    private ArgumentListSyntax ConvertPermissionArguments(ArgumentListSyntax list)
    {
        var arguments = new List<ArgumentSyntax>();
        var position = 0;
        foreach (var argument in list.Arguments)
        {
            if (argument.NameColon is null)
            {
                if (position++ < 2) arguments.Add(argument);
                else Todo($"Positional permission argument '{argument}' does not match ABP.io's AddPermission(name, displayName, multiTenancySide, isEnabled).");
                continue;
            }

            switch (argument.NameColon.Name.Identifier.Text)
            {
                case "multiTenancySides":
                    arguments.Add(argument.WithNameColon(NameColon("multiTenancySide").WithTriviaFrom(argument.NameColon)));
                    break;
                case "featureDependency":
                    Todo($"Feature dependency ({argument.Expression}) → call .RequireFeatures(...) on the permission.");
                    break;
                default:
                    Todo($"Permission argument '{argument.NameColon.Name}' is not supported by ABP.io and was removed.");
                    break;
            }
        }

        var separators = Enumerable.Repeat(Token(SyntaxKind.CommaToken).WithTrailingTrivia(Space), Math.Max(0, arguments.Count - 1));
        return list.WithArguments(SeparatedList(arguments, separators));
    }

    private SyntaxNode? ConvertSettings(MethodDeclarationSyntax node)
    {
        var returned = node.Body!.Statements.OfType<ReturnStatementSyntax>().SingleOrDefault()?.Expression;
        var initializer = returned switch
        {
            ArrayCreationExpressionSyntax a => a.Initializer,
            ImplicitArrayCreationExpressionSyntax i => i.Initializer,
            BaseObjectCreationExpressionSyntax o => o.Initializer,
            CollectionExpressionSyntax c => null,
            _ => null
        };

        var elements = initializer?.Expressions.ToList()
                       ?? (returned as CollectionExpressionSyntax)?.Elements.OfType<ExpressionElementSyntax>().Select(e => e.Expression).ToList();
        if (elements is null || node.Body.Statements.Count != 1)
        {
            Todo("Convert GetSettingDefinitions to 'public override void Define(ISettingDefinitionContext context) { context.Add(...); }'.");
            return null;
        }

        var indent = SyntaxHelpers.Indentation(node);
        var nl = Context.NewLine;
        var definitions = elements.Select(e => indent + "        " + ConvertSettingDefinition(e));
        var code =
            $"{indent}public override void Define(ISettingDefinitionContext context){nl}" +
            $"{indent}{{{nl}" +
            $"{indent}    context.Add({nl}" +
            string.Join("," + nl, definitions) + nl +
            $"{indent}    );{nl}" +
            $"{indent}}}{nl}";

        Context.RequireUsing("Volo.Abp.Settings");
        Context.Change("GetSettingDefinitions → Define(ISettingDefinitionContext)");
        return ParseMemberDeclaration(code)!.WithLeadingTrivia(node.GetLeadingTrivia());
    }

    private string ConvertSettingDefinition(ExpressionSyntax expression)
    {
        if (expression is not ObjectCreationExpressionSyntax { ArgumentList: { } args } creation || SyntaxHelpers.SimpleName(creation.Type) != "SettingDefinition")
        {
            return SyntaxHelpers.Text(expression);
        }

        var converted = new List<string>();
        var position = 0;
        foreach (var argument in args.Arguments)
        {
            if (argument.NameColon is null)
            {
                if (position++ < 3) converted.Add(SyntaxHelpers.Text(argument));
                else Todo($"Positional setting argument '{argument}' must be converted manually.");
                continue;
            }

            var name = argument.NameColon.Name.Identifier.Text;
            switch (name)
            {
                case "name" or "defaultValue" or "displayName" or "description" or "isInherited" or "isEncrypted" or "isVisibleToClients":
                    converted.Add(SyntaxHelpers.Text(argument));
                    break;
                case "clientVisibilityProvider":
                    converted.Add(argument.Expression.ToString().Contains("Visible", StringComparison.Ordinal) &&
                                  !argument.Expression.ToString().Contains("Hidden", StringComparison.Ordinal)
                        ? "isVisibleToClients: true"
                        : "isVisibleToClients: false");
                    break;
                case "scopes":
                    Context.Change("Setting scopes removed (ABP.io resolves values through setting value providers)");
                    break;
                default:
                    Todo($"Setting argument '{name}' is not supported by ABP.io and was removed.");
                    break;
            }
        }

        // A named argument may appear only once (clientVisibilityProvider and isVisibleToClients both map to isVisibleToClients).
        var distinct = converted
            .GroupBy(a => a.Contains(':') && !a.TrimStart().StartsWith('"') ? a[..a.IndexOf(':')].Trim() : Guid.NewGuid().ToString())
            .Select(g => g.First());
        return $"new SettingDefinition({string.Join(", ", distinct)})";
    }
}
