using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.CodeMigration.Rewriters;

/// <summary>
/// L("Key") → L["Key"]  (ABP.io's L is an IStringLocalizer),
/// LocalizationSourceName = X → LocalizationResource = typeof(XResource),
/// new LocalizableString("Key", Source) → LocalizableString.Create&lt;XResource&gt;("Key").
/// </summary>
public sealed class LocalizationRewriter : MigrationRewriter
{
    private readonly Stack<bool> _classDeclaresL = new();

    public LocalizationRewriter(FileMigrationContext context) : base(context)
    {
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        // Permission providers etc. often declare their own "static ILocalizableString L(string name)";
        // ABP.io definition providers have no L property, so L(...) is never an IStringLocalizer call there.
        _classDeclaresL.Push(
            node.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.Text == "L" && !m.Modifiers.Any(SyntaxKind.OverrideKeyword)) ||
            SyntaxHelpers.HasBaseType(node, "AuthorizationProvider", "SettingProvider", "FeatureProvider", "NavigationProvider",
                "PermissionDefinitionProvider", "SettingDefinitionProvider", "FeatureDefinitionProvider"));
        var result = base.VisitClassDeclaration(node);
        _classDeclaresL.Pop();
        return result;
    }

    /// <summary>
    /// "protected override string L(string name)" overrode AbpServiceBase.L; ABP.io services have an L property
    /// (IStringLocalizer) instead, so the override is removed.
    /// </summary>
    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        if (node.Identifier.Text == "L" && node.Modifiers.Any(SyntaxKind.OverrideKeyword))
        {
            Context.Change("Removed override of L(...) (ABP.io services have the L localizer property)");
            return null;
        }

        return base.VisitMethodDeclaration(node);
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        if (visited.Expression is not IdentifierNameSyntax { Identifier.Text: "L" } l ||
            (_classDeclaresL.Count > 0 && _classDeclaresL.Peek()) ||
            visited.ArgumentList.Arguments.Count == 0)
        {
            return visited;
        }

        Context.Change("L(\"key\") → L[\"key\"]");
        var arguments = BracketedArgumentList(visited.ArgumentList.Arguments)
            .WithOpenBracketToken(Token(SyntaxKind.OpenBracketToken).WithTriviaFrom(visited.ArgumentList.OpenParenToken))
            .WithCloseBracketToken(Token(SyntaxKind.CloseBracketToken).WithTriviaFrom(visited.ArgumentList.CloseParenToken));
        var element = ElementAccessExpression(l, arguments).WithTriviaFrom(visited);

        // L("x").Replace(...) worked on a string; L["x"] is a LocalizedString → L["x"].Value.Replace(...)
        if (node.Parent is MemberAccessExpressionSyntax parent && parent.Expression == node &&
            parent.Name.Identifier.Text is not ("Value" or "Name" or "ResourceNotFound" or "SearchedLocation"))
        {
            return MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, element.WithoutTrailingTrivia(), IdentifierName("Value"))
                .WithTrailingTrivia(element.GetTrailingTrivia());
        }

        return element;
    }

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        var visited = (AssignmentExpressionSyntax)base.VisitAssignmentExpression(node)!;
        if (!IsIdentifier(visited.Left, "LocalizationSourceName"))
        {
            return visited;
        }

        if (Context.Localization is not { } loc)
        {
            Todo("Set LocalizationResource = typeof(YourResource) (ABP.io uses typed localization resources).");
            return visited;
        }

        Context.RequireUsing(loc.Namespace);
        Context.Change($"LocalizationSourceName → LocalizationResource = typeof({loc.TypeName})");
        return visited
            .WithLeft(IdentifierName("LocalizationResource").WithTriviaFrom(visited.Left))
            .WithRight(SyntaxHelpers.Expr($"typeof({loc.TypeName})").WithTriviaFrom(visited.Right));
    }

    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var visited = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node)!;
        if (SyntaxHelpers.SimpleName(visited.Type) != "LocalizableString" || visited.ArgumentList?.Arguments.Count != 2)
        {
            return visited;
        }

        if (Context.Localization is not { } loc)
        {
            Todo("Use LocalizableString.Create<YourResource>(\"Key\").");
            return visited;
        }

        Context.RequireUsing("Volo.Abp.Localization");
        Context.RequireUsing(loc.Namespace);
        Context.Change($"new LocalizableString(key, source) → LocalizableString.Create<{loc.TypeName}>(key)");
        var key = visited.ArgumentList.Arguments[0].ToString();
        return SyntaxHelpers.Expr($"LocalizableString.Create<{loc.TypeName}>({key})").WithTriviaFrom(visited);
    }
}
