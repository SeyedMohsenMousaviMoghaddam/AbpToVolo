using AbpToVolo.CodeMigration;
using AbpToVolo.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.Steps;

/// <summary>
/// ABP.io module entities (IdentityUser, IdentityRole, Tenant) created and changed the Boilerplate way:
///   new IdentityUser { UserName = u, Email = e, TenantId = t, IsActive = true, PeopleId = p }
///   → new IdentityUser(GuidGenerator.Create(), (u) ?? string.Empty, (e) ?? string.Empty, t)
///         .SetProtected(e => e.IsActive, true).SetProperty("PeopleId", p)
///   new Tenant() → ModuleEntityFactory.NewTenant(GuidGenerator.Create()) (its constructor is internal)
///   role.Name = n → role.SetProtected(e => e.Name, n) + NormalizedName; user.Roles = new List&lt;…&gt;() → removed
/// Members without a public setter go through SetProtected; members the entity does not have (Module Zero columns kept
/// as extra properties) through SetProperty.
/// </summary>
public sealed partial class BuildFixStep
{
    private sealed record ModuleEntity(string[] PublicMembers, string[] Collections);

    private static readonly Dictionary<string, ModuleEntity> ModuleEntities = new(StringComparer.Ordinal)
    {
        ["IdentityUser"] = new(["Name", "Surname", "ConcurrencyStamp"], ["Roles", "Claims", "Logins", "Tokens", "OrganizationUnits", "PasswordHistories"]),
        ["IdentityRole"] = new(["IsDefault", "IsStatic", "IsPublic", "ConcurrencyStamp"], ["Claims"]),
        ["Tenant"] = new(["ConcurrencyStamp"], ["ConnectionStrings"]),
    };

    /// <summary>Members the constructor takes (IdentityUser(id, userName, email, tenantId), IdentityRole(id, name, tenantId)).</summary>
    private static readonly Dictionary<string, string[]> ConstructorMembers = new(StringComparer.Ordinal)
    {
        ["IdentityUser"] = ["Id", "UserName", "Email", "TenantId"],
        ["IdentityRole"] = ["Id", "Name", "TenantId"],
        ["Tenant"] = ["Id"],
    };

    /// <summary>The members ABP.io's entities have (anything else in an initializer is a Module Zero column → extra property).</summary>
    private static readonly Dictionary<string, HashSet<string>> ModuleEntityMembers = new(StringComparer.Ordinal)
    {
        ["IdentityUser"] = new(StringComparer.Ordinal)
        {
            "Id", "TenantId", "UserName", "NormalizedUserName", "Name", "Surname", "Email", "NormalizedEmail", "EmailConfirmed", "PasswordHash",
            "SecurityStamp", "IsExternal", "PhoneNumber", "PhoneNumberConfirmed", "IsActive", "TwoFactorEnabled", "LockoutEnd", "LockoutEnabled",
            "AccessFailedCount", "ShouldChangePasswordOnNextLogin", "EntityVersion", "LastPasswordChangeTime", "ConcurrencyStamp",
            "CreationTime", "CreatorId", "LastModificationTime", "LastModifierId", "IsDeleted", "DeleterId", "DeletionTime",
            "Roles", "Claims", "Logins", "Tokens", "OrganizationUnits", "PasswordHistories", "ExtraProperties"
        },
        ["IdentityRole"] = new(StringComparer.Ordinal)
        {
            "Id", "TenantId", "Name", "NormalizedName", "IsDefault", "IsStatic", "IsPublic", "EntityVersion", "ConcurrencyStamp", "Claims", "ExtraProperties"
        },
        ["Tenant"] = new(StringComparer.Ordinal)
        {
            "Id", "Name", "NormalizedName", "EntityVersion", "ConcurrencyStamp", "ConnectionStrings", "ExtraProperties",
            "CreationTime", "CreatorId", "LastModificationTime", "LastModifierId", "IsDeleted", "DeleterId", "DeletionTime"
        },
    };

    [System.Text.RegularExpressions.GeneratedRegex(@"'(?<type>IdentityUser|IdentityRole|Tenant)\.\k<type>\(\)' is inaccessible")]
    private static partial System.Text.RegularExpressions.Regex InaccessibleModuleEntityConstructor();

    /// <summary>The creation of a module entity around the token (its constructor or a member of its initializer).</summary>
    private static (ObjectCreationExpressionSyntax Creation, string Type)? ModuleEntityCreation(SyntaxToken token)
    {
        var creation = token.Parent?.AncestorsAndSelf().OfType<ObjectCreationExpressionSyntax>().FirstOrDefault(c => ModuleEntities.ContainsKey(SyntaxHelpers.SimpleName(c.Type)));
        if (creation is null || creation.ArgumentList is { Arguments.Count: > 0 }) return null;
        // The token is the type, or the target of an initializer member (not a nested expression's member).
        var inTarget = creation.Type.Span.Contains(token.Span) ||
                       creation.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>().Any(a => a.Left.Span.Contains(token.Span)) == true;
        return inTarget ? (creation, SyntaxHelpers.SimpleName(creation.Type)) : null;
    }

    internal static PlannedFix? PlanModuleEntityCreation(MigrationContext context, SyntaxToken token)
    {
        if (ModuleEntityCreation(token) is not var (creation, type)) return null;
        if (type == "IdentityUser" && UserById(creation) is { } userById) return userById;
        if (CompatShimStep.EnsureProtectedSetterShim(context) is not { } ns) return null;
        if (type == "Tenant" && CompatShimStep.EnsureModuleEntityFactoryShim(context) is null) return null;
        return new PlannedFix(creation, n => RewriteModuleEntityCreation((ObjectCreationExpressionSyntax)n, type).WithTriviaFrom(n),
            $"new {type} {{ … }} → ABP.io constructor + SetProtected / SetProperty", [ns, "Volo.Abp.Data"]);
    }

    internal static ExpressionSyntax RewriteModuleEntityCreation(ObjectCreationExpressionSyntax creation, string type)
    {
        var members = creation.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is IdentifierNameSyntax)
            .Select(a => (Name: ((IdentifierNameSyntax)a.Left).Identifier.Text, Value: a.Right.WithoutTrivia().ToString()))
            .ToList() ?? [];
        string? Value(string name) => members.FirstOrDefault(m => m.Name == name).Value;
        var id = Value("Id") ?? "GuidGenerator.Create()";

        var constructor = type switch
        {
            "IdentityUser" => $"new IdentityUser({id}, ({Value("UserName") ?? "null"}) ?? string.Empty, ({Value("Email") ?? "null"}) ?? string.Empty, {Value("TenantId") ?? "null"})",
            "IdentityRole" => $"new IdentityRole({id}, ({Value("Name") ?? "null"}) ?? string.Empty, {Value("TenantId") ?? "null"})",
            _ => $"ModuleEntityFactory.NewTenant({id})"
        };

        var entity = ModuleEntities[type];
        var known = ModuleEntityMembers[type];
        var rest = members.Where(m => !ConstructorMembers[type].Contains(m.Name)).ToList();
        // Public members stay in the initializer (not after a factory call).
        var initializer = type == "Tenant" ? [] : rest.Where(m => entity.PublicMembers.Contains(m.Name)).ToList();
        var result = constructor + (initializer.Count > 0 ? " { " + string.Join(", ", initializer.Select(m => $"{m.Name} = {m.Value}")) + " }" : "");
        foreach (var (name, value) in rest.Except(initializer))
        {
            if (entity.Collections.Contains(name)) continue; // ABP.io creates the collections
            result = known.Contains(name)
                ? $"{result}.SetProtected(e => e.{name}, {value})" + (type != "IdentityUser" && name == "Name" ? $".SetProtected(e => e.NormalizedName, ({value})?.ToUpperInvariant())" : "")
                : $"{result}.SetProperty(\"{name}\", {value})";
        }

        return ParseExpression(result);
    }

    private static readonly HashSet<string> ZeroManagers = new(StringComparer.Ordinal) { "IdentityRoleManager", "RoleManager", "IdentityUserManager", "UserManager" };

    private static readonly HashSet<string> ZeroPermissionMethods = new(StringComparer.Ordinal)
    {
        "GetGrantedPermissionsAsync", "SetGrantedPermissionsAsync", "GrantPermissionAsync", "ProhibitPermissionAsync", "ResetAllPermissionsAsync", "IsGrantedAsync"
    };

    /// <summary>
    /// Methods of Module Zero's managers ABP.io's do not have:
    ///   roleManager.SetGrantedPermissionsAsync(role, permissions) → BoilerplatePermissionManager (same methods, ABP.io grants);
    ///   userManager.GetUserByIdAsync(id) → GetByIdAsync(id); tenantManager.FindByTenancyNameAsync(name) → BoilerplateTenantLookup.
    /// </summary>
    private static PlannedFix? PlanZeroManagerCall(MigrationContext context, SyntaxToken token, string type, string member)
    {
        if (token.Parent is not IdentifierNameSyntax { Parent: MemberAccessExpressionSyntax access } name || access.Name != name ||
            access.Parent is not InvocationExpressionSyntax)
        {
            return null;
        }

        if (ZeroManagers.Contains(type) && ZeroPermissionMethods.Contains(member) && CompatShimStep.EnsurePermissionManagerShim(context) is { } permissionNs)
        {
            return new PlannedFix(access.Expression, n => ParseExpression("LazyServiceProvider.LazyGetRequiredService<BoilerplatePermissionManager>()").WithTriviaFrom(n),
                $"{type}.{member} → BoilerplatePermissionManager.{member} (ABP.io permission grants)", [permissionNs]);
        }

        if (type is "IdentityUserManager" or "UserManager" && member == "GetUserByIdAsync")
        {
            return new PlannedFix(name, n => IdentifierName("GetByIdAsync").WithTriviaFrom(n), $"{type}.GetUserByIdAsync → GetByIdAsync", []);
        }

        if (type is "IdentityRoleManager" or "RoleManager" && member == "GetRoleByNameAsync")
        {
            return new PlannedFix(name, n => IdentifierName("FindByNameAsync").WithTriviaFrom(n), $"{type}.GetRoleByNameAsync → FindByNameAsync", []);
        }

        if (type is "ITenantManager" or "TenantManager" && member is "FindByTenancyNameAsync" or "FindByTenancyName" &&
            CompatShimStep.EnsureModuleEntityFactoryShim(context) is { } lookupNs)
        {
            return new PlannedFix(access, n => ((MemberAccessExpressionSyntax)n)
                    .WithExpression(ParseExpression("LazyServiceProvider.LazyGetRequiredService<BoilerplateTenantLookup>()"))
                    .WithName(IdentifierName(member.EndsWith("Async", StringComparison.Ordinal) ? member : member + "Async")),
                $"{type}.{member} → BoilerplateTenantLookup (tenancy name kept as an extra property)", [lookupNs]);
        }

        return null;
    }

    /// <summary>
    /// Module Zero entity members ABP.io's do not have: user.SetNormalizedNames() / role.SetNormalizedName() → the normalized
    /// names set with SetProtected; role.Permissions = new List&lt;…&gt;() (Zero's permission collection) → removed.
    /// </summary>
    private static PlannedFix? PlanZeroEntityMember(MigrationContext context, SyntaxToken token, string type, string member)
    {
        if (!ModuleEntities.ContainsKey(type) || token.Parent is not IdentifierNameSyntax { Parent: MemberAccessExpressionSyntax access } name || access.Name != name ||
            access.Ancestors().OfType<ExpressionStatementSyntax>().FirstOrDefault() is not { } statement)
        {
            return null;
        }

        var receiver = access.Expression.WithoutTrivia().ToString();
        if (member is "SetNormalizedNames" or "SetNormalizedName" && statement.Expression is InvocationExpressionSyntax &&
            CompatShimStep.EnsureProtectedSetterShim(context) is { } ns)
        {
            var normalized = type == "IdentityUser"
                ? $"{receiver}.SetProtected(e => e.NormalizedUserName, {receiver}.UserName?.ToUpperInvariant()).SetProtected(e => e.NormalizedEmail, {receiver}.Email?.ToUpperInvariant());"
                : $"{receiver}.SetProtected(e => e.NormalizedName, {receiver}.Name?.ToUpperInvariant());";
            return new PlannedFix(statement, n => ParseStatement(normalized).WithTriviaFrom(n), $"{type}.{member}() → normalized names with SetProtected", [ns]);
        }

        if (member == "Permissions" && statement.Expression is AssignmentExpressionSyntax { Left: var left, Right: ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax } &&
            left == access)
        {
            return new PlannedFix(statement, n => ParseStatement("{ }")
                .WithLeadingTrivia(n.GetLeadingTrivia().Add(Comment($"/* AbpToVolo: ABP.io keeps permissions as grants (BoilerplatePermissionManager) — removed: {SyntaxHelpers.Text(n)} */")).Add(Space))
                .WithTrailingTrivia(n.GetTrailingTrivia()), $"{type}.Permissions = new … removed (ABP.io permission grants)", []);
        }

        return null;
    }

    /// <summary>
    /// UnitOfWorkManager.Begin(new UnitOfWorkOptions { Scope = TransactionScopeOption.RequiresNew, IsTransactional = true,
    /// IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted, Timeout = t })
    /// → Begin(new AbpUnitOfWorkOptions { IsTransactional = true, IsolationLevel = System.Data.IsolationLevel.ReadCommitted,
    ///   Timeout = (int)(t).TotalMilliseconds }, requiresNew: true)
    /// </summary>
    private static PlannedFix? PlanUnitOfWorkOptions(SyntaxToken token)
    {
        if (token.Parent?.AncestorsAndSelf().OfType<ObjectCreationExpressionSyntax>().FirstOrDefault(c => SyntaxHelpers.SimpleName(c.Type) == "UnitOfWorkOptions")
                is not { Parent: ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax begin } } } creation)
        {
            return null;
        }

        return new PlannedFix(begin, n =>
        {
            var invocation = (InvocationExpressionSyntax)n;
            var options = invocation.ArgumentList.Arguments
                .Select(a => a.Expression).OfType<ObjectCreationExpressionSyntax>().First(c => SyntaxHelpers.SimpleName(c.Type) == "UnitOfWorkOptions");
            var members = new List<string>();
            var requiresNew = false;
            foreach (var a in options.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>() ?? [])
            {
                var value = a.Right.WithoutTrivia().ToString();
                switch (a.Left.ToString())
                {
                    case "Scope":
                        requiresNew = value.EndsWith("RequiresNew", StringComparison.Ordinal) || value.EndsWith("Suppress", StringComparison.Ordinal);
                        if (value.EndsWith("Suppress", StringComparison.Ordinal)) members.Add("IsTransactional = false");
                        break;
                    case "IsolationLevel":
                        members.Add($"IsolationLevel = {value.Replace("System.Transactions.IsolationLevel.", "System.Data.IsolationLevel.").Replace("Transactions.IsolationLevel.", "System.Data.IsolationLevel.")}");
                        break;
                    case "Timeout":
                        members.Add($"Timeout = (int)({value}).TotalMilliseconds");
                        break;
                    case "IsTransactional":
                        members.Add($"IsTransactional = {value}");
                        break;
                }
            }

            var abpOptions = $"new AbpUnitOfWorkOptions {{ {string.Join(", ", members)} }}";
            return ParseExpression($"{invocation.Expression}({abpOptions}{(requiresNew ? ", requiresNew: true" : "")})").WithTriviaFrom(n);
        }, "UnitOfWorkOptions → AbpUnitOfWorkOptions (Scope RequiresNew → requiresNew: true)", ["Volo.Abp.Uow"]);
    }

    private static readonly Dictionary<string, string> CrudPermissionChecks = new(StringComparer.Ordinal)
    {
        ["CheckGetPermission"] = "CheckGetPolicyAsync", ["CheckGetAllPermission"] = "CheckGetListPolicyAsync",
        ["CheckCreatePermission"] = "CheckCreatePolicyAsync", ["CheckUpdatePermission"] = "CheckUpdatePolicyAsync",
        ["CheckDeletePermission"] = "CheckDeletePolicyAsync",
    };

    /// <summary>CheckDeletePermission() of Boilerplate's CRUD app services → await CheckDeletePolicyAsync() (ABP.io CrudAppService).</summary>
    private static PlannedFix? PlanCrudPermissionCheck(SyntaxToken token)
    {
        if (token.Parent is not IdentifierNameSyntax { Parent: InvocationExpressionSyntax { Parent: ExpressionStatementSyntax statement } } name ||
            !CrudPermissionChecks.TryGetValue(name.Identifier.Text, out var policy))
        {
            return null;
        }

        var method = statement.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);
        var isAsync = method switch
        {
            BaseMethodDeclarationSyntax m => m.Modifiers.Any(SyntaxKind.AsyncKeyword),
            LocalFunctionStatementSyntax f => f.Modifiers.Any(SyntaxKind.AsyncKeyword),
            AnonymousFunctionExpressionSyntax f => f.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword),
            _ => false
        };
        return new PlannedFix(statement, n => ParseStatement(isAsync ? $"await {policy}();" : $"global::Volo.Abp.Threading.AsyncHelper.RunSync(() => {policy}());")
            .WithTriviaFrom(n), $"{name.Identifier.Text}() → {policy}()", []);
    }

    /// <summary>new EntityDto&lt;int&gt;(id): abstract in ABP.io → the generated BoilerplateEntityDto&lt;int&gt; (an EntityDto&lt;int&gt;).</summary>
    private static PlannedFix? PlanAbstractEntityDto(MigrationContext context, SyntaxToken token)
    {
        if (token.Parent?.AncestorsAndSelf().OfType<ObjectCreationExpressionSyntax>().FirstOrDefault() is not { Type: GenericNameSyntax { Identifier.Text: "EntityDto" } type } ||
            CompatShimStep.EnsureEntityDtoShim(context) is not { } ns)
        {
            return null;
        }

        return new PlannedFix(type, n => ((GenericNameSyntax)n).WithIdentifier(Identifier("BoilerplateEntityDto").WithTriviaFrom(((GenericNameSyntax)n).Identifier)),
            "new EntityDto<T>(id) → new BoilerplateEntityDto<T>(id) (EntityDto<T> is abstract in ABP.io)", [ns]);
    }

    private static readonly HashSet<string> ZeroSettingStoreMethods = new(StringComparer.Ordinal)
    {
        "GetSettingOrNull", "GetSettingOrNullAsync", "Create", "CreateAsync", "Update", "UpdateAsync", "Delete", "DeleteAsync"
    };

    /// <summary>
    /// _settingStore.GetSettingOrNull(tenantId, userId, name) / Create / Update (Module Zero's ISettingStore): the class's
    /// ISettingStore becomes the generated BoilerplateSettingStore (same methods, over ISettingManager).
    /// </summary>
    private static PlannedFix? PlanSettingStoreType(SyntaxToken token, string type, string member)
    {
        if (type != "ISettingStore" || !ZeroSettingStoreMethods.Contains(member) ||
            token.Parent?.AncestorsAndSelf().OfType<ClassDeclarationSyntax>().FirstOrDefault() is not { } cls)
        {
            return null;
        }

        return new PlannedFix(cls, n => n.ReplaceNodes(
                n.DescendantNodes().OfType<IdentifierNameSyntax>().Where(i => i.Identifier.Text == "ISettingStore" && i.Parent is not (MemberAccessExpressionSyntax or QualifiedNameSyntax)),
                (_, i) => IdentifierName("BoilerplateSettingStore").WithTriviaFrom(i)),
            "Module Zero's ISettingStore → BoilerplateSettingStore", []);
    }

    /// <summary>
    /// .SetProtected(e => e.CreatorId, sourceUserId) — CS0411: an audit user id (Guid?) set from a source user id (long):
    /// the value becomes the ABP.io id, LegacyUserIds.ToUserGuid(value) (--id-type source).
    /// </summary>
    private static PlannedFix? PlanAuditUserIdValue(MigrationContext context, SyntaxToken token)
    {
        if (context.Options.IdType != IdTypeMode.Source || context.SolutionName is not { } ns ||
            token.Parent?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault(i =>
                i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "SetProtected" } &&
                i.ArgumentList.Arguments is [{ Expression: SimpleLambdaExpressionSyntax { Body: MemberAccessExpressionSyntax { Name.Identifier.Text: "CreatorId" or "LastModifierId" or "DeleterId" } } }, _])
                is not { } invocation)
        {
            return null;
        }

        var value = invocation.ArgumentList.Arguments[1];
        return new PlannedFix(value, n => ((ArgumentSyntax)n).WithExpression(ParseExpression($"global::{ns}.LegacyUserIds.ToUserGuid({((ArgumentSyntax)n).Expression.WithoutTrivia()})")),
            "audit user id set from a source user id → LegacyUserIds.ToUserGuid", []);
    }

    /// <summary>GetContext() of Boilerplate's EfCoreRepositoryBase → (await GetDbContextAsync()) (ABP.io EfCoreRepository).</summary>
    private static PlannedFix? PlanRepositoryContext(SyntaxToken token)
    {
        if (token.Parent is not IdentifierNameSyntax { Identifier.Text: "GetContext" or "GetDbContext", Parent: InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } invocation }) return null;
        var method = invocation.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);
        var isAsync = method switch
        {
            BaseMethodDeclarationSyntax m => m.Modifiers.Any(SyntaxKind.AsyncKeyword),
            LocalFunctionStatementSyntax f => f.Modifiers.Any(SyntaxKind.AsyncKeyword),
            AnonymousFunctionExpressionSyntax f => f.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword),
            _ => false
        };
        return new PlannedFix(invocation,
            n => ParseExpression(isAsync ? "(await GetDbContextAsync())" : "global::Volo.Abp.Threading.AsyncHelper.RunSync(() => GetDbContextAsync())").WithTriviaFrom(n),
            "GetContext() → GetDbContextAsync()", []);
    }

    /// <summary>TenantManager.GetByIdAsync(id) (Boilerplate's AbpTenantManager) → ITenantRepository.GetAsync(id).</summary>
    private static PlannedFix? PlanTenantManagerGetById(SyntaxToken token)
    {
        var invocation = token.Parent?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "GetByIdAsync" or "GetById", Expression: var receiver } &&
                                 receiver.ToString().EndsWith("TenantManager", StringComparison.Ordinal));
        if (invocation?.Expression is not MemberAccessExpressionSyntax access) return null;
        return new PlannedFix(access, n => ((MemberAccessExpressionSyntax)n)
                .WithExpression(ParseExpression("LazyServiceProvider.LazyGetRequiredService<ITenantRepository>()"))
                .WithName(IdentifierName("GetAsync")),
            "TenantManager.GetByIdAsync → ITenantRepository.GetAsync", ["Volo.Abp.TenantManagement"]);
    }

    /// <summary>
    /// x.Member = value on a module entity without a public setter: role/tenant Name also sets NormalizedName, a new
    /// collection (user.Roles = new List&lt;…&gt;()) is removed (ABP.io creates them), anything else → SetProtected.
    /// </summary>
    private static PlannedFix? PlanModuleEntitySetter(MigrationContext context, ExpressionStatementSyntax statement, string type, string member)
    {
        if (!ModuleEntities.TryGetValue(type, out var entity)) return null;
        if (entity.Collections.Contains(member))
        {
            return new PlannedFix(statement, n => ParseStatement("{ }")
                .WithLeadingTrivia(n.GetLeadingTrivia().Add(Comment($"/* AbpToVolo: ABP.io creates {type}.{member} — removed: {SyntaxHelpers.Text(n)} */")).Add(Space))
                .WithTrailingTrivia(n.GetTrailingTrivia()), $"{type}.{member} = new … removed (ABP.io creates the collection)", []);
        }

        if (CompatShimStep.EnsureProtectedSetterShim(context) is not { } ns) return null;
        return new PlannedFix(statement, n =>
        {
            var a = (AssignmentExpressionSyntax)((ExpressionStatementSyntax)n).Expression;
            var receiver = ((MemberAccessExpressionSyntax)a.Left).Expression.WithoutTrivia();
            var value = a.Right.WithoutTrivia();
            return (type != "IdentityUser" && member == "Name"
                ? ParseStatement($"{{ var __name = {value}; {receiver}.SetProtected(e => e.Name, __name).SetProtected(e => e.NormalizedName, __name?.ToUpperInvariant()); }}")
                : ParseStatement($"{receiver}.SetProtected(e => e.{member}, {value});")).WithTriviaFrom(n);
        }, $"{type}.{member} = … → SetProtected", [ns]);
    }
}
