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
    private const string IdentityUserType = "Volo.Abp.Identity.IdentityUser";
    private const string IdentityRoleType = "Volo.Abp.Identity.IdentityRole";
    private const string LegacyIdProperty = "LegacyId";

    private static readonly HashSet<string> TypeMismatchCodes = new(StringComparer.Ordinal) { "CS0029", "CS0266", "CS0019", "CS1503", "CS0030", "CS0173", "CS1662", "CS1929" };

    private static readonly HashSet<string> RepositoryIdMethods = new(StringComparer.Ordinal)
    {
        "GetAsync", "FindAsync", "DeleteAsync", "Get", "Find", "Delete", "FirstOrDefaultAsync", "SingleAsync", "SingleOrDefaultAsync", "FirstAsync"
    };

    /// <summary>
    /// Fixes of the errors the compiler reports that need the real types: source ids ↔ ABP.io ids (--id-type source),
    /// former navigations of Zero entities (User.People) and Module Zero columns ABP.io entities lack (UserToken.ExpireDate).
    /// </summary>
    private int ApplySemanticFixes(MigrationContext context, List<BuildDiagnostic> errors, Dictionary<string, int> totals)
    {
        if (!context.UsesIdentityModules) return 0;
        var legacyIds = context.Options.IdType == IdTypeMode.Source;
        var applied = 0;
        var relevant = errors.Where(e => legacyIds && (TypeMismatchCodes.Contains(e.Code) || e.Code == "CS1941") ||
                                         e.Code == "CS1061" && MissingMember().Match(e.Message) is { Success: true } m &&
                                         IsZeroMember(context, m.Groups["type"].Value, m.Groups["member"].Value) ||
                                         e.Code is "CS7036" or "CS1729" or "CS1995");
        foreach (var group in relevant.GroupBy(e => e.Project, StringComparer.OrdinalIgnoreCase))
        {
            if (context.Projects.FirstOrDefault(p => string.Equals(p.Name, group.Key, StringComparison.OrdinalIgnoreCase)) is not { } project) continue;
            if (SemanticProject.Load(project.CsprojPath) is not { } semantic) continue;
            if (semantic.Compilation.GetTypeByMetadataName(TenantType) is null) continue;

            var edits = new Dictionary<SyntaxTree, Dictionary<SyntaxNode, SyntaxNode>>();
            var declarations = new Dictionary<BlockSyntax, List<string>>();
            var hoists = new Dictionary<StatementSyntax, List<string>>();
            foreach (var error in group)
            {
                if (!semantic.Trees.TryGetValue(error.File, out var tree)) continue;
                var text = tree.GetText();
                if (error.Line < 1 || error.Line > text.Lines.Count) continue;
                var position = text.Lines[error.Line - 1].Start + Math.Max(0, error.Column - 1);
                var model = semantic.Compilation.GetSemanticModel(tree);
                var node = tree.GetRoot().FindToken(position).Parent;
                if (node is null) continue;

                if (error.Code == "CS1995")
                {
                    PlanHoistAwait(node, edits, hoists);
                    continue;
                }

                if (error.Code is "CS7036" or "CS1729")
                {
                    if (node.AncestorsAndSelf().OfType<ConstructorDeclarationSyntax>().FirstOrDefault() is { Initializer: { } } ctor) PlanBaseConstructor(model, ctor, edits);
                    continue;
                }

                if (error.Code == "CS1061")
                {
                    var member = MissingMember().Match(error.Message);
                    PlanZeroMember(context, semantic, model, node, member.Groups["type"].Value, member.Groups["member"].Value, edits, declarations);
                    continue;
                }

                if (error.Code == "CS1929")
                {
                    PlanLegacyIdCollection(semantic, node, error, edits);
                    continue;
                }

                if (error.Code == "CS1941")
                {
                    PlanLegacyIdJoin(model, node, edits);
                    continue;
                }

                var expression = node.AncestorsAndSelf().OfType<ExpressionSyntax>().TakeWhile(e => e.SpanStart == position).LastOrDefault();
                if (expression is null) continue;

                var before = edits.Values.Sum(e => e.Count);
                PlanTenantIdFixes(semantic, model, expression, edits);
                if (edits.Values.Sum(e => e.Count) == before) PlanTenantIdConversion(semantic, model, node, expression, error, edits);
            }

            applied += WriteEdits(context, edits, totals, "source ids ↔ ABP.io ids, Zero navigations and columns (semantic fixes)", declarations, hoists);
        }

        return applied;
    }

    private static bool IsZeroMember(MigrationContext context, string type, string member)
    {
        var simple = type.Split('.').Last();
        return context.ZeroNavigations.TryGetValue(simple, out var navigations) && navigations.ContainsKey(member) ||
               Mappings.ZeroMappings.MissingEntityColumns.TryGetValue(simple, out var columns) && columns.Any(c => c.Name == member);
    }

    /// <summary>
    /// A member the ABP.io entity does not have:
    ///   - former navigation (user.People): in an EF Core query a subquery on the foreign key
    ///     (__peopleQuery.FirstOrDefault(p => p.Id == EF.Property&lt;int?&gt;(user, "PeopleId"))), elsewhere the repository
    ///     (await repository.FindAsync(user's PeopleId)); in Include(...) the include is removed;
    ///   - Module Zero column kept as an EF shadow property (token.ExpireDate): EF.Property in queries, EntityShadowProperties
    ///     Get / Set elsewhere.
    /// </summary>
    private static void PlanZeroMember(MigrationContext context, SemanticProject semantic, SemanticModel model, SyntaxNode node, string type, string member,
        Dictionary<SyntaxTree, Dictionary<SyntaxNode, SyntaxNode>> edits, Dictionary<BlockSyntax, List<string>> declarations)
    {
        if (node.AncestorsAndSelf().OfType<MemberAccessExpressionSyntax>().FirstOrDefault(a => a.Name.Identifier.Text == member) is not { } access) return;
        var receiver = access.Expression.WithoutTrivia();
        var inQuery = InExpressionTree(model, access);
        var simple = type.Split('.').Last();

        // Module Zero column kept as a shadow property
        if (Mappings.ZeroMappings.MissingEntityColumns.TryGetValue(simple, out var columns) && columns.FirstOrDefault(c => c.Name == member) is { Name: not null } column)
        {
            if (FindType(semantic.Compilation, "EntityShadowProperties") is not { } store) return;
            var storeName = "global::" + store.ToDisplayString();
            if (access.Parent is AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression, Parent: ExpressionStatementSyntax statement } assignment &&
                assignment.Left == access)
            {
                Add(edits, statement, ParseStatement($"{storeName}.Set({receiver}, \"{member}\", {assignment.Right.WithoutTrivia()});").WithTriviaFrom(statement));
            }
            else if (!IsAssignmentTarget(access))
            {
                Add(edits, access, (inQuery
                    ? ParseExpression($"EF.Property<{column.Type}>({receiver}, \"{member}\")")
                    : ParseExpression($"{storeName}.Get<{column.Type}>({receiver}, \"{member}\")")).WithTriviaFrom(access));
            }

            return;
        }

        if (!context.ZeroNavigations.TryGetValue(simple, out var navigations) || !navigations.TryGetValue(member, out var navigation)) return;
        if (IsAssignmentTarget(access)) return;

        // .Include(u => u.People): the navigation does not exist any more → the include goes.
        if (access.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Include" or "ThenInclude" } &&
                i.ArgumentList.Arguments.Any(a => a.Span.Contains(access.Span))) is { Expression: MemberAccessExpressionSyntax include } includeCall)
        {
            Add(edits, includeCall, include.Expression.WithTriviaFrom(includeCall));
            return;
        }

        if (semantic.Compilation.GetTypeByMetadataName(navigation.TargetType) is not { } target) return;
        var key = Members(target).OfType<IPropertySymbol>().FirstOrDefault(p => p.Name == "Id")?.Type.ToDisplayString();
        var method = access.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax);
        var body = method switch { BaseMethodDeclarationSyntax m => m.Body, LocalFunctionStatementSyntax f => f.Body, AccessorDeclarationSyntax a => a.Body, _ => null };
        if (key is null || body is null || model.LookupSymbols(access.SpanStart, name: "LazyServiceProvider").IsEmpty) return;

        var isAsync = method switch { BaseMethodDeclarationSyntax m => m.Modifiers.Any(SyntaxKind.AsyncKeyword), LocalFunctionStatementSyntax f => f.Modifiers.Any(SyntaxKind.AsyncKeyword), _ => false };
        var repository = $"LazyServiceProvider.LazyGetRequiredService<global::Volo.Abp.Domain.Repositories.IRepository<global::{navigation.TargetType}, {key}>>()";
        string Await(string task) => isAsync ? $"await {task}" : $"global::Volo.Abp.Threading.AsyncHelper.RunSync(() => {task})";

        if (inQuery)
        {
            // A subquery on the foreign key (EF Core translates it, also .Member and == null after it).
            var variable = $"__{char.ToLowerInvariant(target.Name[0])}{target.Name[1..]}Query";
            if (!declarations.TryGetValue(body, out var list)) declarations[body] = list = [];
            // Declared once per method (also when an earlier round already added it).
            var declared = body.Statements.OfType<LocalDeclarationStatementSyntax>().Any(s => s.Declaration.Variables.Any(v => v.Identifier.Text == variable));
            if (!declared && !list.Any(d => d.Contains($" {variable} =", StringComparison.Ordinal))) list.Add($"var {variable} = {Await($"{repository}.GetQueryableAsync()")};");
            Add(edits, access, ParseExpression(
                    $"{variable}.FirstOrDefault(__x => ({navigation.ForeignKeyType.TrimEnd('?')}?)__x.Id == EF.Property<{navigation.ForeignKeyType}>({receiver}, \"{navigation.ForeignKey}\"))")
                .WithTriviaFrom(access));
            return;
        }

        // In memory: load the target by its key.
        var foreignKey = $"global::Volo.Abp.Data.HasExtraPropertiesExtensions.GetProperty<{navigation.ForeignKeyType}>({Parenthesize(receiver)}, \"{navigation.ForeignKey}\")";
        var load = navigation.ForeignKeyType.EndsWith('?')
            ? $"({foreignKey} is {{ }} __{navigation.ForeignKey}{access.SpanStart} ? {Await($"{repository}.FindAsync(__{navigation.ForeignKey}{access.SpanStart})")} : null)"
            : $"({Await($"{repository}.FindAsync({foreignKey})")})";
        Add(edits, access, ParseExpression(load).WithTriviaFrom(access));
    }

    /// <summary>
    /// CS1995: "await" in a query expression outside its first from / join (the converted _repo.GetAll() → await
    /// _repo.GetQueryableAsync()): the awaited queryable goes to a local just before the statement (no query runs there).
    /// </summary>
    private static void PlanHoistAwait(SyntaxNode node, Dictionary<SyntaxTree, Dictionary<SyntaxNode, SyntaxNode>> edits, Dictionary<StatementSyntax, List<string>> hoists)
    {
        if (node.AncestorsAndSelf().OfType<AwaitExpressionSyntax>().FirstOrDefault() is not { } awaited) return;
        ExpressionSyntax target = awaited.Parent is ParenthesizedExpressionSyntax parenthesized ? parenthesized : awaited;
        if (target.Ancestors().OfType<StatementSyntax>().FirstOrDefault(s => s.Parent is BlockSyntax) is not { } statement) return;

        var name = $"__query{awaited.SpanStart}";
        if (!hoists.TryGetValue(statement, out var list)) hoists[statement] = list = [];
        if (list.Any(s => s.StartsWith($"var {name} ", StringComparison.Ordinal))) return;
        list.Add($"var {name} = {awaited.WithoutTrivia()};");
        Add(edits, target, IdentifierName(name).WithTriviaFrom(target));
    }

    /// <summary>
    /// A subclass of an ABP.io type (a Zero manager kept as a subclass of IdentityUserManager…) whose ": base(...)" does not
    /// match: the parameters of the ABP.io constructor (the widest one) come first, its own dependencies after them.
    /// </summary>
    private static void PlanBaseConstructor(SemanticModel model, ConstructorDeclarationSyntax ctor, Dictionary<SyntaxTree, Dictionary<SyntaxNode, SyntaxNode>> edits)
    {
        if (ctor.Parent is not ClassDeclarationSyntax cls || model.GetDeclaredSymbol(cls)?.BaseType is not { } baseType ||
            !baseType.ContainingNamespace.ToDisplayString().StartsWith("Volo.", StringComparison.Ordinal))
        {
            return;
        }

        var baseCtor = baseType.InstanceConstructors
            .Where(c => c.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal)
            .OrderByDescending(c => c.Parameters.Length).FirstOrDefault();
        if (baseCtor is null) return;

        var own = ctor.ParameterList.Parameters.Select(p => p.Identifier.Text).ToHashSet(StringComparer.Ordinal);
        var indent = SyntaxHelpers.Indentation(ctor) + "    ";
        var nl = ctor.SyntaxTree.GetText().ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var baseParameters = baseCtor.Parameters
            .Select(p => (Name: own.Contains(p.Name) ? p.Name + "Base" : p.Name, Type: p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)))
            .ToList();
        var parameters = baseParameters.Select(p => Parameter(Identifier(p.Name)).WithType(ParseTypeName(p.Type).WithTrailingTrivia(Space)))
            .Concat(ctor.ParameterList.Parameters.Select(p => p.WithoutTrivia()))
            .Select(p => p.WithLeadingTrivia(EndOfLine(nl), Whitespace(indent)))
            .ToList();
        var parameterList = ParameterList(SeparatedList(parameters, Enumerable.Repeat(Token(SyntaxKind.CommaToken), parameters.Count - 1)));
        var arguments = ArgumentList(SeparatedList(baseParameters.Select(p => Argument(IdentifierName(p.Name))),
            Enumerable.Repeat(Token(SyntaxKind.CommaToken).WithTrailingTrivia(Space), Math.Max(0, baseParameters.Count - 1))));
        var initializer = ctor.Initializer!.WithArgumentList(arguments)
            .WithColonToken(Token(SyntaxKind.ColonToken).WithLeadingTrivia(EndOfLine(nl), Whitespace(indent)).WithTrailingTrivia(Space));
        Add(edits, ctor, ctor.WithParameterList(parameterList.WithTrailingTrivia(ctor.ParameterList.GetTrailingTrivia())).WithInitializer(initializer));
    }

    private static IEnumerable<ISymbol> Members(INamedTypeSymbol type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            foreach (var m in t.GetMembers()) yield return m;
        }
    }

    private static void PlanTenantIdFixes(SemanticProject semantic, SemanticModel model, ExpressionSyntax expression, Dictionary<SyntaxTree, Dictionary<SyntaxNode, SyntaxNode>> edits)
    {
        // repository.DeleteAsync(input.Id) on a Tenant/IdentityUser/IdentityRole repository → DeleteAsync(t => EF.Property<int>(t, "LegacyId") == input.Id)
        if (expression.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Arguments.Count: 1, Parent: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax call } } } &&
            RepositoryIdMethods.Contains(call.Name.Identifier.Text) && LegacyRepositoryKey(model.GetTypeInfo(call.Expression).Type) is { } repositoryKey &&
            model.GetTypeInfo(expression).Type?.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64)
        {
            var predicate = ParseExpression($"__t => EF.Property<{repositoryKey}>(__t, \"{LegacyIdProperty}\") == {expression.WithoutTrivia()}");
            Add(edits, expression, predicate.WithTriviaFrom(expression));
            return;
        }

        foreach (var access in expression.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().Where(m => m.Name.Identifier.Text == "Id"))
        {
            if (IsAssignmentTarget(access)) continue;
            if (LegacyKey(model.GetTypeInfo(access.Expression).Type) is { } key)
            {
                Add(edits, access, LegacyAccess(model, access, access.Expression, key));
                continue;
            }

            // x.Id of an anonymous type whose Id is a tenant's Id: new { t.Id } → new { Id = <legacy id of t> }
            if (model.GetSymbolInfo(access).Symbol is IPropertySymbol { ContainingType.IsAnonymousType: true } property)
            {
                PlanAnonymousTenantId(semantic, property, edits, depth: 0);
            }
        }
    }

    /// <summary>
    /// A source id (DTOs/models: tenant int, user long) meets an ABP.io id (Guid: entities, CurrentTenant.Id, CurrentUser.Id):
    ///   tenant: int → Guid LegacyTenantIds.ToTenantGuid, Guid → int ToLegacyTenantId (only where the code mentions "tenant");
    ///   user:   long → Guid LegacyUserIds.ToUserGuid, Guid → long ToLegacyUserId (user ids are the only long ids).
    /// </summary>
    private static void PlanTenantIdConversion(SemanticProject semantic, SemanticModel model, SyntaxNode node, ExpressionSyntax expression, BuildDiagnostic error,
        Dictionary<SyntaxTree, Dictionary<SyntaxNode, SyntaxNode>> edits)
    {
        static bool IsGuid(string type) => type.TrimEnd('?') is "System.Guid" or "Guid";
        static string? Legacy(string type) => type.TrimEnd('?') switch { "int" or "System.Int32" => "tenant", "long" or "System.Int64" => "user", _ => null };

        ExpressionSyntax? target = null;
        string? kind = null;
        var toGuid = false;
        if (ConversionMessage().Match(error.Message) is { Success: true } conversion)
        {
            var (from, to) = (conversion.Groups["from"].Value, conversion.Groups["to"].Value);
            target = error.Code == "CS1503" ? expression.AncestorsAndSelf().OfType<ArgumentSyntax>().FirstOrDefault()?.Expression ?? expression : expression;
            if (IsGuid(from) && Legacy(to) is { } toKind) kind = toKind;
            else if (Legacy(from) is { } fromKind && IsGuid(to)) (kind, toGuid) = (fromKind, true);
        }
        else if (error.Code == "CS0019" && OperatorMessage().Match(error.Message) is { Success: true } op &&
                 node.AncestorsAndSelf().OfType<BinaryExpressionSyntax>().FirstOrDefault(b => b.OperatorToken.Text == op.Groups["op"].Value) is { } binary)
        {
            // Compare as Guid: the source-typed side (a DTO / parameter value) is converted.
            var (a, b) = (op.Groups["a"].Value, op.Groups["b"].Value);
            if (IsGuid(a) && Legacy(b) is { } right) (target, kind, toGuid) = (binary.Right, right, true);
            else if (Legacy(a) is { } left && IsGuid(b)) (target, kind, toGuid) = (binary.Left, left, true);
        }

        if (target is null || kind is null) return;

        // (int?)x.RoleId: the cast stays, its operand is converted.
        if (error.Code == "CS0030" && target is CastExpressionSyntax cast) target = cast.Expression;

        // int ids: tenants or roles — the code tells (the argument's method, the assignment, the comparison…).
        if (kind == "tenant")
        {
            var context = (SyntaxNode?)target.AncestorsAndSelf().FirstOrDefault(n => n is StatementSyntax or AnonymousObjectMemberDeclaratorSyntax or
                AssignmentExpressionSyntax or ArgumentSyntax or BinaryExpressionSyntax) ?? target;
            // An argument: the method it is passed to tells (CurrentTenant.Change(1), SetTenantId(id)…).
            var about = context is ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax invocation } ? invocation.Expression + context.ToString() : context.ToString();
            var own = target.ToString();
            if (own.Contains("role", StringComparison.OrdinalIgnoreCase) && !own.Contains("tenant", StringComparison.OrdinalIgnoreCase)) kind = "role";
            else if (own.Contains("tenant", StringComparison.OrdinalIgnoreCase) || about.Contains("tenant", StringComparison.OrdinalIgnoreCase)) kind = "tenant";
            else if (about.Contains("role", StringComparison.OrdinalIgnoreCase)) kind = "role";
            else return;
        }

        // Generated in Domain.Shared (a referenced project) by LegacyIdStep.
        var (helperType, method) = kind switch
        {
            "tenant" => ("LegacyTenantIds", toGuid ? "ToTenantGuid" : "ToLegacyTenantId"),
            "role" => ("LegacyRoleIds", toGuid ? "ToRoleGuid" : "ToLegacyRoleId"),
            _ => ("LegacyUserIds", toGuid ? "ToUserGuid" : "ToLegacyUserId")
        };
        if (FindType(semantic.Compilation, helperType) is not { } helper) return;

        // guids.Contains(x.RoleId) in an EF Core query: LegacyRoleIds.ToRoleGuid(x.RoleId) cannot be translated, so the in-memory
        // collection is converted instead (guids.Select(__id => LegacyRoleIds.ToLegacyRoleId(__id)).ToArray(), a query parameter).
        if (toGuid && target.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Arguments.Count: 1, Parent: InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Contains" } contains
            } } } &&
            !IsQueryable(model.GetTypeInfo(contains.Expression).Type) && InExpressionTree(model, target))
        {
            var reverse = kind switch { "tenant" => "ToLegacyTenantId", "role" => "ToLegacyRoleId", _ => "ToLegacyUserId" };
            Add(edits, contains.Expression, ParseExpression(
                    $"{Parenthesize(contains.Expression.WithoutTrivia())}.Select(__id => global::{helper.ToDisplayString()}.{reverse}(__id)).ToArray()")
                .WithTriviaFrom(contains.Expression));
            return;
        }

        Add(edits, target, ParseExpression($"global::{helper.ToDisplayString()}.{method}({target.WithoutTrivia()})").WithTriviaFrom(target));
    }

    /// <summary>
    /// CS1941 — join … on x.RoleId equals r.Id: a source id (long/int) against the Guid key of an ABP.io user/role/tenant
    /// → join … on x.RoleId equals EF.Property&lt;int&gt;(r, "LegacyId").
    /// </summary>
    internal static void PlanLegacyIdJoin(SemanticModel model, SyntaxNode node, Dictionary<SyntaxTree, Dictionary<SyntaxNode, SyntaxNode>> edits)
    {
        if (node.AncestorsAndSelf().OfType<JoinClauseSyntax>().FirstOrDefault() is not { } join) return;
        if (ElementType(model.GetTypeInfo(join.InExpression).Type) is not { } element ||
            element.ToDisplayString() is not (IdentityUserType or IdentityRoleType or TenantType))
        {
            return;
        }

        var variable = join.Identifier.Text;
        foreach (var (key, other) in new[] { (join.RightExpression, join.LeftExpression), (join.LeftExpression, join.RightExpression) })
        {
            if (key is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Id", Expression: IdentifierNameSyntax owner } || owner.Identifier.Text != variable) continue;
            if (model.GetTypeInfo(other).Type is not { } otherType) return;
            var type = otherType.ToDisplayString();
            if (type is not ("long" or "int" or "long?" or "int?")) return;
            Add(edits, key, ParseExpression($"EF.Property<{type}>({variable}, \"{LegacyIdProperty}\")").WithTriviaFrom(key));
            return;
        }
    }

    private static ITypeSymbol? ElementType(ITypeSymbol? type) => type switch
    {
        INamedTypeSymbol { IsGenericType: true, TypeArguments: [var element] } named when named.Name is "IQueryable" or "IEnumerable" or "List" or "DbSet" => element,
        INamedTypeSymbol named => named.AllInterfaces.FirstOrDefault(i => i.Name == "IQueryable" && i.IsGenericType)?.TypeArguments[0],
        _ => null
    };

    /// <summary>
    /// ids.Contains(x.UserId) with source ids (long[] / List&lt;int&gt;… from a DTO) and an ABP.io Guid: the collection is converted
    /// (ids.Select(__id => LegacyUserIds.ToUserGuid(__id)).ToArray()) — evaluated on the client, a parameter of the query.
    /// </summary>
    private static void PlanLegacyIdCollection(SemanticProject semantic, SyntaxNode node, BuildDiagnostic error,
        Dictionary<SyntaxTree, Dictionary<SyntaxNode, SyntaxNode>> edits)
    {
        if (CollectionMessage().Match(error.Message) is not { Success: true } match || match.Groups["method"].Value != "Contains" ||
            node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Contains" })
                is not { Expression: MemberAccessExpressionSyntax contains } invocation)
        {
            return;
        }

        var element = match.Groups["element"].Value.TrimEnd('?');
        var argument = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString() ?? "";
        var (helperType, method) = element switch
        {
            "long" => ("LegacyUserIds", "ToUserGuid"),
            "int" when argument.Contains("Role", StringComparison.Ordinal) => ("LegacyRoleIds", "ToRoleGuid"),
            "int" when argument.Contains("Tenant", StringComparison.Ordinal) => ("LegacyTenantIds", "ToTenantGuid"),
            _ => ((string?)null, (string?)null)
        };
        if (helperType is null || FindType(semantic.Compilation, helperType) is not { } helper) return;

        var receiver = contains.Expression.WithoutTrivia();
        var converted = ParseExpression($"{Parenthesize(receiver)}.Select(__id => global::{helper.ToDisplayString()}.{method}(__id)).ToArray()");
        Add(edits, contains.Expression, converted.WithTriviaFrom(contains.Expression));
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"'(?:[\w.]+<)?(?<element>[\w.]+\??)(\[\]|>)' does not contain a definition for '(?<method>\w+)'")]
    private static partial System.Text.RegularExpressions.Regex CollectionMessage();

    /// <summary>A type of the compilation (its own sources or a referenced project) by its simple name.</summary>
    private static INamedTypeSymbol? FindType(Compilation compilation, string name)
    {
        static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol ns) =>
            ns.GetTypeMembers().Concat(ns.GetNamespaceMembers().SelectMany(Types));

        return compilation.SourceModule.ReferencedAssemblySymbols
            .Where(a => !a.Name.StartsWith("Volo.", StringComparison.Ordinal) && !a.Name.StartsWith("Microsoft.", StringComparison.Ordinal) &&
                        !a.Name.StartsWith("System", StringComparison.Ordinal))
            .Select(a => a.GlobalNamespace)
            .Prepend(compilation.Assembly.GlobalNamespace)
            .SelectMany(Types)
            .FirstOrDefault(t => t.Name == name);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:Cannot implicitly convert type|cannot convert from|Cannot convert type) '(?<from>[^']+)' to '(?<to>[^']+)'")]
    private static partial System.Text.RegularExpressions.Regex ConversionMessage();

    [System.Text.RegularExpressions.GeneratedRegex(@"Operator '(?<op>[^']+)' cannot be applied to operands of type '(?<a>[^']+)' and '(?<b>[^']+)'")]
    private static partial System.Text.RegularExpressions.Regex OperatorMessage();

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
                if (LegacyKey(model.GetTypeInfo(source.Expression).Type) is { } key)
                {
                    var name = declarator.NameEquals?.Name.Identifier.Text ?? "Id";
                    Add(edits, declarator, SyntaxHelpers.NamedMember(declarator, name, LegacyAccess(model, source, source.Expression, key)));
                }
                else if (model.GetSymbolInfo(source).Symbol is IPropertySymbol { ContainingType.IsAnonymousType: true } inner)
                {
                    PlanAnonymousTenantId(semantic, inner, edits, depth + 1);
                }
            }
        }
    }

    /// <summary>The tenant's legacy id: EF.Property inside an EF Core query (expression tree), GetLegacyId() elsewhere.</summary>
    private static ExpressionSyntax LegacyAccess(SemanticModel model, SyntaxNode at, ExpressionSyntax receiver, string key) =>
        (InExpressionTree(model, at)
            ? ParseExpression($"EF.Property<{key}>({receiver.WithoutTrivia()}, \"{LegacyIdProperty}\")")
            : ParseExpression($"{Parenthesize(receiver.WithoutTrivia())}.GetLegacyId()"))
        .WithTriviaFrom(at);

    private static string Parenthesize(ExpressionSyntax e) => e is IdentifierNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax or ThisExpressionSyntax ? e.ToString() : $"({e})";

    private static bool IsAssignmentTarget(ExpressionSyntax e) => e.Parent is AssignmentExpressionSyntax assignment && assignment.Left == e;

    /// <summary>The source key type kept as LegacyId by the ABP.io entity (--id-type source), or null.</summary>
    private static string? LegacyKey(ITypeSymbol? type) => type?.OriginalDefinition.ToDisplayString() switch
    {
        TenantType => "int",
        "Volo.Abp.Identity.IdentityRole" => "int",
        "Volo.Abp.Identity.IdentityUser" => "long",
        _ => null
    };

    private static string? LegacyRepositoryKey(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
            ? named.AllInterfaces.Append(named).SelectMany(i => i.TypeArguments).Select(LegacyKey).FirstOrDefault(k => k is not null)
            : null;

    /// <summary>Inside a lambda that is converted to an expression tree (an IQueryable operator: translated to SQL).</summary>
    internal static bool InExpressionTree(SemanticModel model, SyntaxNode node) =>
        node.Ancestors().OfType<LambdaExpressionSyntax>().Any(lambda =>
            model.GetTypeInfo(lambda).ConvertedType is INamedTypeSymbol { Name: "Expression" } converted &&
            converted.ContainingNamespace.ToDisplayString() == "System.Linq.Expressions" ||
            // The lambda does not bind (it contains the error being fixed): an argument of a LINQ operator on an IQueryable.
            lambda.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax call } } } &&
            IsQueryable(model.GetTypeInfo(call.Expression).Type) ||
            // ...or an Expression<...> parameter of the called method (repository.FindAsync(t => ...), GetAsync, DeleteAsync...).
            lambda.Parent is ArgumentSyntax argument && TakesExpression(model, argument)) ||
        // Query syntax (from x in query ... select ...) over an IQueryable.
        node.Ancestors().OfType<QueryExpressionSyntax>().Any(query => IsQueryable(model.GetTypeInfo(query.FromClause.Expression).Type));

    /// <summary>The argument goes to an Expression&lt;...&gt; parameter of every method the call can bind to.</summary>
    private static bool TakesExpression(SemanticModel model, ArgumentSyntax argument)
    {
        if (argument.Parent is not ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation } list) return false;
        var info = model.GetSymbolInfo(invocation);
        var candidates = (info.Symbol is { } symbol ? [symbol] : info.CandidateSymbols).OfType<IMethodSymbol>().ToList();
        if (candidates.Count == 0) candidates = model.GetMemberGroup(invocation.Expression).OfType<IMethodSymbol>().ToList();
        var index = list.Arguments.IndexOf(argument);

        ITypeSymbol? ParameterType(IMethodSymbol method) => (argument.NameColon is { } name
            ? method.Parameters.FirstOrDefault(p => p.Name == name.Name.Identifier.Text)
            : index < method.Parameters.Length ? method.Parameters[index] : null)?.Type;
        static bool IsExpression(ITypeSymbol? type) => type is INamedTypeSymbol { Name: "Expression", IsGenericType: true } named &&
                                                       named.ContainingNamespace.ToDisplayString() == "System.Linq.Expressions";

        // Overloads a lambda can go to (FindAsync(id) does not count): all of them take an expression tree.
        var types = candidates.Select(ParameterType).Where(t => IsExpression(t) || t?.TypeKind == TypeKind.Delegate).ToList();
        return types.Count > 0 && types.All(IsExpression);
    }

    private static bool IsQueryable(ITypeSymbol? type) =>
        type is not null && (type.Name == "IQueryable" || type.AllInterfaces.Any(i => i.Name == "IQueryable" && i.ContainingNamespace.ToDisplayString() == "System.Linq"));

    private static void Add(Dictionary<SyntaxTree, Dictionary<SyntaxNode, SyntaxNode>> edits, SyntaxNode node, SyntaxNode replacement)
    {
        if (!edits.TryGetValue(node.SyntaxTree, out var file)) edits[node.SyntaxTree] = file = [];
        if (file.Keys.Any(k => k.Span.Contains(node.Span) || node.Span.Contains(k.Span))) return;
        file[node] = replacement;
    }

    private int WriteEdits(MigrationContext context, Dictionary<SyntaxTree, Dictionary<SyntaxNode, SyntaxNode>> edits, Dictionary<string, int> totals, string kind,
        Dictionary<BlockSyntax, List<string>>? declarations = null, Dictionary<StatementSyntax, List<string>>? hoists = null)
    {
        var applied = 0;
        foreach (var (tree, replacements) in edits)
        {
            if (replacements.Count == 0 || !File.Exists(tree.FilePath) || context.IsExcluded(tree.FilePath)) continue;
            var (_, encoding) = FileSystem.ReadText(tree.FilePath);

            // Local declarations the replacements use go at the start of their method body ("var __peopleQuery = ...") or
            // just before the statement that uses them (hoisted awaits): statement index in the block → statements to insert.
            var blocks = new Dictionary<BlockSyntax, List<(int Index, string Statement)>>();
            foreach (var (block, statements) in declarations?.Where(d => d.Key.SyntaxTree == tree) ?? [])
            {
                if (!blocks.TryGetValue(block, out var list)) blocks[block] = list = [];
                list.AddRange(statements.Select(s => (0, s)));
            }

            foreach (var (statement, statements) in hoists?.Where(h => h.Key.SyntaxTree == tree && h.Key.Parent is BlockSyntax) ?? [])
            {
                var block = (BlockSyntax)statement.Parent!;
                if (!blocks.TryGetValue(block, out var list)) blocks[block] = list = [];
                list.AddRange(statements.Select(s => (block.Statements.IndexOf(statement), s)));
            }

            var nl = tree.GetText().ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var updated = tree.GetRoot().ReplaceNodes(replacements.Keys.Concat(blocks.Keys).Distinct(), (original, rewritten) =>
            {
                if (original is BlockSyntax block && blocks.TryGetValue(block, out var inserts))
                {
                    var current = replacements.TryGetValue(original, out var replaced) ? (BlockSyntax)replaced : (BlockSyntax)rewritten;
                    var result = current.Statements;
                    foreach (var group in inserts.GroupBy(i => i.Index).OrderByDescending(g => g.Key))
                    {
                        var at = Math.Min(group.Key, result.Count);
                        var indent = at < result.Count ? SyntaxHelpers.Indentation(result[at]) :
                            result.Count > 0 ? SyntaxHelpers.Indentation(result[0]) : SyntaxHelpers.Indentation(current) + "    ";
                        result = result.InsertRange(at, group.Select(i => ParseStatement(i.Statement).WithLeadingTrivia(Whitespace(indent)).WithTrailingTrivia(EndOfLine(nl))));
                    }

                    return current.WithStatements(result);
                }

                return replacements[original];
            }).ToFullString();
            var usings = new List<string>();
            if (replacements.Values.Any(r => r.ToString().Contains("EF.Property<", StringComparison.Ordinal))) usings.Add("Microsoft.EntityFrameworkCore");
            if (blocks.Count > 0) usings.Add("System.Linq");
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
