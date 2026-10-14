using AbpToVolo.CodeMigration;
using AbpToVolo.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.Steps;

/// <summary>
/// Fixes that need the full semantic model of the converted code (real ABP.io / EF Core types, see SemanticProject):
///   - --id-type source: the application keeps the int tenant ids of the source; Tenant.Id (Guid) used where an int is
///     expected → the tenant's LegacyId (EF.Property&lt;int&gt;(t, "LegacyId") inside EF Core queries, t.GetLegacyId()
///     elsewhere), through anonymous projections too; repository.GetAsync/FindAsync/DeleteAsync(intId) → a LegacyId predicate;
///   - entity extensions read inside EF Core queries: x.GetProperty&lt;T&gt;("P") / x.GetLegacyId() cannot be translated to SQL
///     → EF.Property&lt;T&gt;(x, "P").
/// </summary>
public sealed partial class BuildFixStep
{
    private const string TenantType = "Volo.Abp.TenantManagement.Tenant";
    private const string LegacyIdProperty = "LegacyId";

    private static readonly HashSet<string> TypeMismatchCodes = new(StringComparer.Ordinal) { "CS0029", "CS0266", "CS0019", "CS1503", "CS0030", "CS0173", "CS1662" };

    private static readonly HashSet<string> RepositoryIdMethods = new(StringComparer.Ordinal)
    {
        "GetAsync", "FindAsync", "DeleteAsync", "Get", "Find", "Delete", "FirstOrDefaultAsync", "SingleAsync", "SingleOrDefaultAsync", "FirstAsync"
    };

    /// <summary>Tenant ids (int in the source) → LegacyId, for the type errors the compiler reports.</summary>
    private int ApplySemanticFixes(MigrationContext context, List<BuildDiagnostic> errors, Dictionary<string, int> totals)
    {
        if (context.Options.IdType != IdTypeMode.Source || !context.UsesIdentityModules) return 0;
        var applied = 0;
        foreach (var group in errors.Where(e => TypeMismatchCodes.Contains(e.Code)).GroupBy(e => e.Project, StringComparer.OrdinalIgnoreCase))
        {
            if (context.Projects.FirstOrDefault(p => string.Equals(p.Name, group.Key, StringComparison.OrdinalIgnoreCase)) is not { } project) continue;
            if (SemanticProject.Load(project.CsprojPath) is not { } semantic) continue;
            if (semantic.Compilation.GetTypeByMetadataName(TenantType) is null) continue;

            var edits = new Dictionary<SyntaxTree, Dictionary<SyntaxNode, SyntaxNode>>();
            foreach (var error in group)
            {
                if (!semantic.Trees.TryGetValue(error.File, out var tree)) continue;
                var text = tree.GetText();
                if (error.Line < 1 || error.Line > text.Lines.Count) continue;
                var position = text.Lines[error.Line - 1].Start + Math.Max(0, error.Column - 1);
                var model = semantic.Compilation.GetSemanticModel(tree);
                var node = tree.GetRoot().FindToken(position).Parent;
                var expression = node?.AncestorsAndSelf().OfType<ExpressionSyntax>().TakeWhile(e => e.SpanStart == position).LastOrDefault();
                if (expression is null) continue;

                PlanTenantIdFixes(semantic, model, expression, edits);
            }

            applied += WriteEdits(context, edits, totals, "Tenant.Id → LegacyId (the application keeps the int tenant ids of the source)");
        }

        return applied;
    }

    private static void PlanTenantIdFixes(SemanticProject semantic, SemanticModel model, ExpressionSyntax expression, Dictionary<SyntaxTree, Dictionary<SyntaxNode, SyntaxNode>> edits)
    {
        // repository.DeleteAsync(input.Id) on a Tenant repository → DeleteAsync(t => EF.Property<int>(t, "LegacyId") == input.Id)
        if (expression.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Arguments.Count: 1, Parent: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax call } } } &&
            RepositoryIdMethods.Contains(call.Name.Identifier.Text) && IsTenantRepository(model.GetTypeInfo(call.Expression).Type) &&
            model.GetTypeInfo(expression).Type?.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64)
        {
            var predicate = ParseExpression($"__t => EF.Property<int>(__t, \"{LegacyIdProperty}\") == {expression.WithoutTrivia()}");
            Add(edits, expression, predicate.WithTriviaFrom(expression));
            return;
        }

        foreach (var access in expression.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().Where(m => m.Name.Identifier.Text == "Id"))
        {
            if (IsAssignmentTarget(access)) continue;
            if (IsTenant(model.GetTypeInfo(access.Expression).Type))
            {
                Add(edits, access, LegacyAccess(model, access, access.Expression));
                continue;
            }

            // x.Id of an anonymous type whose Id is a tenant's Id: new { t.Id } → new { Id = <legacy id of t> }
            if (model.GetSymbolInfo(access).Symbol is IPropertySymbol { ContainingType.IsAnonymousType: true } property)
            {
                PlanAnonymousTenantId(semantic, property, edits, depth: 0);
            }
        }
    }

    private static void PlanAnonymousTenantId(SemanticProject semantic, IPropertySymbol property, Dictionary<SyntaxTree, Dictionary<SyntaxNode, SyntaxNode>> edits, int depth)
    {
        if (depth > 3) return;
        foreach (var reference in property.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is not AnonymousObjectMemberDeclaratorSyntax declarator) continue;
            var model = semantic.Compilation.GetSemanticModel(declarator.SyntaxTree);
            var value = declarator.Expression;
            if (value is MemberAccessExpressionSyntax { Name.Identifier.Text: "Id" } source)
            {
                if (IsTenant(model.GetTypeInfo(source.Expression).Type))
                {
                    var name = declarator.NameEquals?.Name.Identifier.Text ?? "Id";
                    Add(edits, declarator, SyntaxHelpers.NamedMember(declarator, name, LegacyAccess(model, source, source.Expression)));
                }
                else if (model.GetSymbolInfo(source).Symbol is IPropertySymbol { ContainingType.IsAnonymousType: true } inner)
                {
                    PlanAnonymousTenantId(semantic, inner, edits, depth + 1);
                }
            }
        }
    }

    /// <summary>The tenant's legacy id: EF.Property inside an EF Core query (expression tree), GetLegacyId() elsewhere.</summary>
    private static ExpressionSyntax LegacyAccess(SemanticModel model, SyntaxNode at, ExpressionSyntax receiver) =>
        (InExpressionTree(model, at)
            ? ParseExpression($"EF.Property<int>({receiver.WithoutTrivia()}, \"{LegacyIdProperty}\")")
            : ParseExpression($"{Parenthesize(receiver.WithoutTrivia())}.GetLegacyId()"))
        .WithTriviaFrom(at);

    private static string Parenthesize(ExpressionSyntax e) => e is IdentifierNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax or ThisExpressionSyntax ? e.ToString() : $"({e})";

    private static bool IsAssignmentTarget(ExpressionSyntax e) => e.Parent is AssignmentExpressionSyntax assignment && assignment.Left == e;

    private static bool IsTenant(ITypeSymbol? type) => type?.OriginalDefinition.ToDisplayString() == TenantType;

    private static bool IsTenantRepository(ITypeSymbol? type) =>
        type is INamedTypeSymbol named && named.AllInterfaces.Append(named).Any(i => i.TypeArguments.Any(IsTenant));

    /// <summary>Inside a lambda that is converted to an expression tree (an IQueryable operator: translated to SQL).</summary>
    private static bool InExpressionTree(SemanticModel model, SyntaxNode node) =>
        node.Ancestors().OfType<LambdaExpressionSyntax>().Any(lambda =>
            model.GetTypeInfo(lambda).ConvertedType is INamedTypeSymbol { Name: "Expression" } converted &&
            converted.ContainingNamespace.ToDisplayString() == "System.Linq.Expressions");

    private static void Add(Dictionary<SyntaxTree, Dictionary<SyntaxNode, SyntaxNode>> edits, SyntaxNode node, SyntaxNode replacement)
    {
        if (!edits.TryGetValue(node.SyntaxTree, out var file)) edits[node.SyntaxTree] = file = [];
        if (file.Keys.Any(k => k.Span.Contains(node.Span) || node.Span.Contains(k.Span))) return;
        file[node] = replacement;
    }

    private int WriteEdits(MigrationContext context, Dictionary<SyntaxTree, Dictionary<SyntaxNode, SyntaxNode>> edits, Dictionary<string, int> totals, string kind)
    {
        var applied = 0;
        foreach (var (tree, replacements) in edits)
        {
            if (replacements.Count == 0 || !File.Exists(tree.FilePath) || context.IsExcluded(tree.FilePath)) continue;
            var (_, encoding) = FileSystem.ReadText(tree.FilePath);
            var updated = tree.GetRoot().ReplaceNodes(replacements.Keys, (original, _) => replacements[original]).ToFullString();
            var usings = new List<string>();
            if (replacements.Values.Any(r => r.ToString().Contains("EF.Property<", StringComparison.Ordinal))) usings.Add("Microsoft.EntityFrameworkCore");
            FileSystem.WriteText(tree.FilePath, ModuleMethodEditor.AddUsings(updated, usings), encoding);
            totals[kind] = totals.GetValueOrDefault(kind) + replacements.Count;
            context.Report.Change(Name, $"{replacements.Count} × {kind}", context.Relative(tree.FilePath));
            applied += replacements.Count;
        }

        return applied;
    }

    /// <summary>
    /// Entity extensions read inside EF Core queries (x.GetProperty&lt;T&gt;("P"), user.GetLegacyId()): not translatable to SQL
    /// → EF.Property&lt;T&gt;(x, "P"). Runs on the projects that compile; returns the number of rewritten calls.
    /// </summary>
    private int FixExtensionPropertiesInQueries(MigrationContext context, Dictionary<string, int> totals)
    {
        if (!context.UsesIdentityModules) return 0;
        var applied = 0;
        foreach (var project in context.Projects.Where(p => p.Layer is not (ProjectLayer.Tests or ProjectLayer.DomainShared or ProjectLayer.ApplicationContracts)))
        {
            var sources = FileSystem.EnumerateFiles(project.Directory, "*.cs").Where(f => !context.IsExcluded(f)).ToList();
            if (!sources.Any(f => File.ReadAllText(f) is var s && (s.Contains(".GetProperty<", StringComparison.Ordinal) || s.Contains(".GetLegacyId()", StringComparison.Ordinal)))) continue;
            if (SemanticProject.Load(project.CsprojPath) is not { } semantic || semantic.Compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.EF") is null) continue;

            var edits = new Dictionary<SyntaxTree, Dictionary<SyntaxNode, SyntaxNode>>();
            foreach (var tree in semantic.Trees.Values.Where(t => sources.Contains(t.FilePath, StringComparer.OrdinalIgnoreCase)))
            {
                var model = semantic.Compilation.GetSemanticModel(tree);
                foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (invocation.Expression is not MemberAccessExpressionSyntax { Name: var name } member) continue;
                    var method = name.Identifier.Text;
                    if (method is not ("GetProperty" or "GetLegacyId") || !InExpressionTree(model, invocation)) continue;
                    if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol symbol) continue;

                    string? propertyName = null;
                    var type = symbol.ReturnType.ToDisplayString();
                    if (method == "GetProperty" && symbol.ContainingType.ToDisplayString() == "Volo.Abp.Data.HasExtraPropertiesExtensions" &&
                        invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal && name is GenericNameSyntax)
                    {
                        propertyName = literal.Token.ValueText;
                    }
                    else if (method == "GetLegacyId" && symbol.ContainingType.Name == "LegacyIds" &&
                             symbol.Parameters.FirstOrDefault()?.Type.Name is "IdentityUser" or "IdentityRole" or "Tenant")
                    {
                        propertyName = LegacyIdProperty;
                    }

                    if (propertyName is null) continue;
                    Add(edits, invocation, ParseExpression($"EF.Property<{type}>({member.Expression.WithoutTrivia()}, \"{propertyName}\")").WithTriviaFrom(invocation));
                }
            }

            applied += WriteEdits(context, edits, totals, "entity extension read inside an EF Core query → EF.Property");
        }

        return applied;
    }
}
