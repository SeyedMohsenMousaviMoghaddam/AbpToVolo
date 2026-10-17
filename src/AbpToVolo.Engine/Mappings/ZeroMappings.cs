using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using AbpToVolo.Core;

namespace AbpToVolo.Mappings;

public enum ZeroKind
{
    User,
    Role,
    Tenant,
    UserManager,
    RoleManager,
    TenantManager,
    UserStore,
    RoleStore,
    LogInManager,
    SignInManager,
    ClaimsPrincipalFactory,
    PermissionChecker,
    SecurityStampValidator,
    FeatureValueStore,
    EditionManager
}

public sealed record ZeroReplacement(string Name, string Namespace);

/// <summary>Module Zero → ABP.io module types (Identity, TenantManagement, PermissionManagement...).</summary>
public static class ZeroMappings
{
    /// <summary>
    /// The Module Zero entity a numeric id refers to, by naming convention — or null. User (long), tenant and role (int)
    /// and organization unit (long) ids are Guid in ABP.io. Shared by the code conversion (TypeNameRewriter) and the
    /// database conversion (ZeroForeignKeys), so the model and the database columns stay consistent.
    /// Returns "Tenant", "User", "Role" or "OrganizationUnit" (the EntityType of Zero_IdMap).
    /// </summary>
    public static string? GuidIdKind(string name, string type, IdTypeMode mode = IdTypeMode.Guid)
    {
        var numeric = type.TrimEnd('?');
        if (numeric is not ("int" or "long")) return null;
        if (name.EndsWith("TenantId", StringComparison.Ordinal) || name == "tenantId") return "Tenant";

        // ABP.io module fields are Guid in both modes: the audit fields (CreatorUserId → CreatorId...). With IdTypeMode.Source
        // the application's own references to users/roles/organization units keep their type (linked through LegacyId).
        if (TypeMappings.MemberRenames.ContainsKey(name)) return numeric == "long" ? "User" : null;
        if (mode == IdTypeMode.Source) return null;
        if (name.EndsWith("UserId", StringComparison.Ordinal) || name == "userId") return numeric == "long" ? "User" : null;
        if (name.EndsWith("RoleId", StringComparison.Ordinal) || name == "roleId") return "Role";
        if (name.EndsWith("OrganizationUnitId", StringComparison.Ordinal) || name == "organizationUnitId") return "OrganizationUnit";
        return null;
    }

    /// <summary>Base class name in Module Zero → kind of the deriving solution class.</summary>
    public static readonly IReadOnlyDictionary<string, ZeroKind> BaseTypes = new Dictionary<string, ZeroKind>(StringComparer.Ordinal)
    {
        ["AbpUser"] = ZeroKind.User,
        ["AbpUserBase"] = ZeroKind.User,
        ["AbpRole"] = ZeroKind.Role,
        ["AbpRoleBase"] = ZeroKind.Role,
        ["AbpTenant"] = ZeroKind.Tenant,
        ["AbpTenantBase"] = ZeroKind.Tenant,
        ["AbpUserManager"] = ZeroKind.UserManager,
        ["AbpRoleManager"] = ZeroKind.RoleManager,
        ["AbpTenantManager"] = ZeroKind.TenantManager,
        ["AbpUserStore"] = ZeroKind.UserStore,
        ["AbpRoleStore"] = ZeroKind.RoleStore,
        ["AbpLogInManager"] = ZeroKind.LogInManager,
        ["AbpLoginManager"] = ZeroKind.LogInManager,
        ["AbpSignInManager"] = ZeroKind.SignInManager,
        ["AbpUserClaimsPrincipalFactory"] = ZeroKind.ClaimsPrincipalFactory,
        ["PermissionChecker"] = ZeroKind.PermissionChecker,
        ["AbpSecurityStampValidator"] = ZeroKind.SecurityStampValidator,
        ["AbpFeatureValueStore"] = ZeroKind.FeatureValueStore,
        ["AbpEditionManager"] = ZeroKind.EditionManager,
    };

    /// <summary>What replaces each kind. Null: no replacement — code that needs it moves to the legacy folder.</summary>
    public static readonly IReadOnlyDictionary<ZeroKind, ZeroReplacement?> Replacements = new Dictionary<ZeroKind, ZeroReplacement?>
    {
        [ZeroKind.User] = new("IdentityUser", "Volo.Abp.Identity"),
        [ZeroKind.Role] = new("IdentityRole", "Volo.Abp.Identity"),
        [ZeroKind.Tenant] = new("Tenant", "Volo.Abp.TenantManagement"),
        [ZeroKind.UserManager] = new("IdentityUserManager", "Volo.Abp.Identity"),
        [ZeroKind.RoleManager] = new("IdentityRoleManager", "Volo.Abp.Identity"),
        [ZeroKind.TenantManager] = new("ITenantManager", "Volo.Abp.TenantManagement"),
        [ZeroKind.UserStore] = new("IdentityUserStore", "Volo.Abp.Identity"),
        [ZeroKind.RoleStore] = new("IdentityRoleStore", "Volo.Abp.Identity"),
        // LogInManager.LoginAsync(...) → AbpLoginResult: the generated compatibility class (CompatShimStep), in the solution root namespace.
        [ZeroKind.LogInManager] = new("LogInManager", ""),
        [ZeroKind.SignInManager] = new("AbpSignInManager", "Volo.Abp.Identity.AspNetCore"),
        [ZeroKind.ClaimsPrincipalFactory] = new("AbpUserClaimsPrincipalFactory", "Volo.Abp.Identity"),
        [ZeroKind.PermissionChecker] = new("IPermissionChecker", "Volo.Abp.Authorization.Permissions"),
        [ZeroKind.SecurityStampValidator] = null,
        [ZeroKind.FeatureValueStore] = null,
        [ZeroKind.EditionManager] = null,
    };

    /// <summary>Zero entity kinds whose primary key becomes Guid (long/int in Boilerplate).</summary>
    public static bool IsEntity(ZeroKind kind) => kind is ZeroKind.User or ZeroKind.Role or ZeroKind.Tenant;

    /// <summary>Module Zero framework types (not declared in the solution) that have an ABP.io counterpart.</summary>
    public static readonly IReadOnlyDictionary<string, ZeroReplacement> FrameworkTypes = new Dictionary<string, ZeroReplacement>(StringComparer.Ordinal)
    {
        ["UserRole"] = new("IdentityUserRole", "Volo.Abp.Identity"),
        ["UserLogin"] = new("IdentityUserLogin", "Volo.Abp.Identity"),
        ["UserClaim"] = new("IdentityUserClaim", "Volo.Abp.Identity"),
        ["RoleClaim"] = new("IdentityRoleClaim", "Volo.Abp.Identity"),
        ["UserToken"] = new("IdentityUserToken", "Volo.Abp.Identity"),
        ["OrganizationUnit"] = new("OrganizationUnit", "Volo.Abp.Identity"),
        ["UserOrganizationUnit"] = new("IdentityUserOrganizationUnit", "Volo.Abp.Identity"),
        ["OrganizationUnitRole"] = new("OrganizationUnitRole", "Volo.Abp.Identity"),
        // RolePermissionSetting, UserPermissionSetting, PermissionSetting keep their names: read-only entities of the same
        // shape over AbpPermissionGrants (ZeroColumnsStep).
        ["UserIdentifier"] = new("Guid", "System"),
    };

    /// <summary>Zero-only infrastructure: code using these (unresolved) names cannot compile on ABP.io and is moved to the legacy folder.</summary>
    public static readonly HashSet<string> LegacyTriggers = new(StringComparer.Ordinal)
    {
        "UserLoginAttempt", "UserLoginAttemptResult", "Edition", "AbpEditionManager",
        "IdentityRegistrar", "AbpUserConfigurationBuilder", "AbpZeroDbMigrator", "IAbpZeroDbMigrator", "AbpCommonHub",
        "IOnlineClientManager", "OnlineClientManager", "DefaultEditionCreator", "AbpZeroTemplateConsts", "AbpZeroDbContext",
        "IAbpZeroConfig", "AbpZeroSettingNames", "ImpersonationManager", "IUserTokenExpirationValidator"
    };

    /// <summary>Classes of the Boilerplate template that are replaced by an ABP.io application module.</summary>
    public static readonly IReadOnlyDictionary<string, string> TemplateServices = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["UserAppService"] = "Identity module (IIdentityUserAppService, /api/identity/users)",
        ["IUserAppService"] = "Identity module (IIdentityUserAppService)",
        ["RoleAppService"] = "Identity module (IIdentityRoleAppService, /api/identity/roles)",
        ["IRoleAppService"] = "Identity module (IIdentityRoleAppService)",
        ["TenantAppService"] = "TenantManagement module (ITenantAppService, /api/multi-tenancy/tenants)",
        ["ITenantAppService"] = "TenantManagement module (ITenantAppService)",
        ["SessionAppService"] = "/api/abp/application-configuration (current user, tenant, localization, permissions)",
        ["ISessionAppService"] = "/api/abp/application-configuration",
        ["AccountAppService"] = "Account module (IAccountAppService: register, password reset)",
        ["IAccountAppService"] = "Account module (IAccountAppService)",
        ["ConfigurationAppService"] = "Setting management module",
        ["IConfigurationAppService"] = "Setting management module",
        ["TokenAuthController"] = "OpenIddict (/connect/token), see the HttpApi.Host module",
        ["TokenAuthConfiguration"] = "OpenIddict",
        ["AuthConfigurer"] = "OpenIddict validation in the HttpApi.Host module",
        ["JwtTokenMiddleware"] = "OpenIddict validation",
        ["HostRoleAndUserCreator"] = "IdentityDataSeedContributor (admin user/role are seeded by the Identity module)",
        ["TenantRoleAndUserBuilder"] = "IdentityDataSeedContributor",
        ["DefaultTenantBuilder"] = "TenantManagement module",
        ["DefaultLanguagesCreator"] = "AbpLocalizationOptions.Languages",
        ["DefaultSettingsCreator"] = "Setting definitions (default values)",
        ["DefaultEditionCreator"] = "SaaS module (commercial) or a custom implementation",
        ["InitialHostDbBuilder"] = "IDataSeeder (ABP.io DbMigrator)",
        ["SeedHelper"] = "IDataSeeder (ABP.io DbMigrator)",
        ["AppRoleConfig"] = "Static roles: IdentityRole with IsStatic = true",
        ["UserRegistrationManager"] = "Account module (registration) / IdentityUserManager.CreateAsync",
        ["AbpLoginResultTypeHelper"] = "SignInResult of ASP.NET Core Identity",
        ["ExternalAuthManager"] = "ASP.NET Core external authentication / Account module",
        ["ExternalAuthConfiguration"] = "ASP.NET Core external authentication",
    };

    /// <summary>
    /// Template class names that are common words: they only match in the namespace the Boilerplate template uses
    /// (e.g. "AccountAppService" of an accounting module must stay).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> TemplateNamespaces = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["UserAppService"] = ["Users"], ["IUserAppService"] = ["Users"],
        ["RoleAppService"] = ["Roles"], ["IRoleAppService"] = ["Roles"],
        ["TenantAppService"] = ["MultiTenancy", "Tenants"], ["ITenantAppService"] = ["MultiTenancy", "Tenants"],
        ["SessionAppService"] = ["Sessions"], ["ISessionAppService"] = ["Sessions"],
        ["AccountAppService"] = ["Authorization.Accounts"], ["IAccountAppService"] = ["Authorization.Accounts"],
        ["ConfigurationAppService"] = ["Configuration"], ["IConfigurationAppService"] = ["Configuration"],
        ["TokenAuthController"] = ["Controllers"],
    };

    /// <summary>
    /// HTTP endpoints (application services, controllers and their interfaces): they are never moved out of the build, even
    /// when they come from the template or use Module Zero infrastructure — the clients call them. The code that does
    /// not compile is stubbed member by member instead, so every route of the source stays.
    /// </summary>
    public static bool IsEndpointType(BaseTypeDeclarationSyntax type)
    {
        var name = type.Identifier.Text;
        if (type is InterfaceDeclarationSyntax)
        {
            return name.EndsWith("AppService", StringComparison.Ordinal) || name.EndsWith("ApplicationService", StringComparison.Ordinal);
        }

        if (type is not ClassDeclarationSyntax) return false;
        if (name.EndsWith("AppService", StringComparison.Ordinal) || name.EndsWith("ApplicationService", StringComparison.Ordinal) ||
            name.EndsWith("Controller", StringComparison.Ordinal))
        {
            return true;
        }

        return type.BaseList?.Types.Any(t => t.Type.ToString() is var b &&
            (b.EndsWith("AppServiceBase", StringComparison.Ordinal) || b.EndsWith("ControllerBase", StringComparison.Ordinal) ||
             b.Contains("ApplicationService", StringComparison.Ordinal) || b.Contains("AppService<", StringComparison.Ordinal) ||
             b is "AbpController" or "Controller")) == true;
    }

    public static bool HasEndpointType(SyntaxNode root) =>
        root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Any(IsEndpointType);

    public static bool IsTemplateClass(string name, string? ns)
    {
        if (!TemplateServices.ContainsKey(name)) return false;
        if (!TemplateNamespaces.TryGetValue(name, out var suffixes)) return true;
        return ns is not null && suffixes.Any(s => ns.EndsWith("." + s, StringComparison.Ordinal) || ns == s);
    }

    /// <summary>Boilerplate base-class members → IdentityUser/IdentityRole/Tenant members.</summary>
    public static readonly IReadOnlyDictionary<ZeroKind, IReadOnlyDictionary<string, string>> MemberRenames = new Dictionary<ZeroKind, IReadOnlyDictionary<string, string>>
    {
        [ZeroKind.User] = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EmailAddress"] = "Email", ["NormalizedEmailAddress"] = "NormalizedEmail", ["IsEmailConfirmed"] = "EmailConfirmed",
            ["IsPhoneNumberConfirmed"] = "PhoneNumberConfirmed", ["IsLockoutEnabled"] = "LockoutEnabled",
            ["IsTwoFactorEnabled"] = "TwoFactorEnabled", ["Password"] = "PasswordHash", ["LockoutEndDateUtc"] = "LockoutEnd",
        },
        // Role: Name stays the unique name (permission grants use it); DisplayName is kept as an extension (MissingBaseProperties).
        // Tenant: Name stays the display name; TenancyName becomes an entity extension (see MissingBaseProperties).
    };

    /// <summary>Boilerplate base-class members without an ABP.io counterpart: kept as entity extensions (name, type, default).</summary>
    public static readonly IReadOnlyDictionary<ZeroKind, (string Name, string Type, string? Default)[]> MissingBaseProperties =
        new Dictionary<ZeroKind, (string, string, string?)[]>
        {
            // Boilerplate tenants have TenancyName (identifier) and Name (display name): ABP.io has only Name. The display name
            // stays Name (the code reading tenant.Name keeps its meaning), TenancyName is kept as an extension.
            [ZeroKind.Tenant] = [("TenancyName", "string", null), ("IsActive", "bool", "true")],
            // Boilerplate user members ABP.io IdentityUser does not have.
            [ZeroKind.User] = [("PasswordResetCode", "string", null), ("EmailConfirmationCode", "string", null), ("AuthenticationSource", "string", null)],
            [ZeroKind.Role] = [("DisplayName", "string", null)],
        };

    /// <summary>
    /// Module Zero columns of entities that ABP.io cannot extend (not IHasExtraProperties): kept as EF Core shadow properties
    /// on the same column (ABP entity → (property, C# type)). The code reads/writes them with EntityShadowProperties.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Name, string Type)[]> MissingEntityColumns =
        new Dictionary<string, (string, string)[]>(StringComparer.Ordinal)
        {
            ["IdentityUserToken"] = [("ExpireDate", "DateTime?")],
        };

    /// <summary>Base class of each entity kind, for the inherited constants (ZeroConstants table).</summary>
    public static string? ConstantsBase(ZeroKind kind) => kind switch
    {
        ZeroKind.User => "AbpUserBase",
        ZeroKind.Role => "AbpRoleBase",
        ZeroKind.Tenant => "AbpTenantBase",
        _ => null
    };
}
