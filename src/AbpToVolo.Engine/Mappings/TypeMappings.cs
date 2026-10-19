namespace AbpToVolo.Mappings;

public sealed record TypeRename(string NewName, string? Namespace = null, string? Todo = null, int? MaxArity = null);

public static class TypeMappings
{
    /// <summary>Boilerplate type name → ABP.io type name. Applied to identifiers and generic names.</summary>
    public static readonly IReadOnlyDictionary<string, TypeRename> Renames = new Dictionary<string, TypeRename>(StringComparer.Ordinal)
    {
        ["IAbpSession"] = new("ICurrentUser", "Volo.Abp.Users"),
        ["AsyncCrudAppService"] = new("CrudAppService", "Volo.Abp.Application.Services", MaxArity: 6),
        ["IAsyncCrudAppService"] = new("ICrudAppService", "Volo.Abp.Application.Services", MaxArity: 5),
        ["CrudAppService"] = new("CrudAppService", "Volo.Abp.Application.Services", MaxArity: 6),
        ["ICrudAppService"] = new("ICrudAppService", "Volo.Abp.Application.Services", MaxArity: 5),
        ["IEventBus"] = new("ILocalEventBus", "Volo.Abp.EventBus.Local"),
        ["IAsyncEventHandler"] = new("ILocalEventHandler", "Volo.Abp.EventBus"),
        ["IEventHandler"] = new("ILocalEventHandler", "Volo.Abp.EventBus"),
        // The method bodies are converted by DefinitionProviderRewriter (which reports what it cannot convert).
        ["AuthorizationProvider"] = new("PermissionDefinitionProvider", "Volo.Abp.Authorization.Permissions"),
        ["SettingProvider"] = new("SettingDefinitionProvider", "Volo.Abp.Settings"),
        ["ISettingManager"] = new("ISettingManager", "Volo.Abp.SettingManagement"),
        // TenantId properties are converted to Guid? by TypeNameRewriter.
        ["IMustHaveTenant"] = new("IMultiTenant", "Volo.Abp.MultiTenancy"),
        ["IMayHaveTenant"] = new("IMultiTenant", "Volo.Abp.MultiTenancy"),
        ["EfCoreRepositoryBase"] = new("EfCoreRepository", "Volo.Abp.Domain.Repositories.EntityFrameworkCore"),
        ["AbpIntegratedTestBase"] = new("AbpIntegratedTest", "Volo.Abp.Testing"),
        ["AbpMvcAuthorizeAttribute"] = new("AuthorizeAttribute", "Microsoft.AspNetCore.Authorization"),
        ["ILocalizationManager"] = new("IStringLocalizerFactory", "Microsoft.Extensions.Localization"),
        ["NameValueDto"] = new("NameValue", "Volo.Abp"),
        // Abp.EntityHistory.EntityChangeType: same values (Created 0, Updated 1, Deleted 2).
        ["EntityChangeType"] = new("EntityChangeType", "Volo.Abp.Auditing"),
        // Boilerplate's ICurrentUnitOfWorkProvider.Current → IUnitOfWorkManager.Current (the same IUnitOfWork members used).
        ["ICurrentUnitOfWorkProvider"] = new("IUnitOfWorkManager", "Volo.Abp.Uow"),
        ["IAbpAntiForgeryManager"] = new("IAbpAntiForgeryManager", "Volo.Abp.AspNetCore.Mvc.AntiForgery"),
        ["AbpAuthorizeAttribute"] = new("AuthorizeAttribute", "Microsoft.AspNetCore.Authorization"),
        ["IAbpAuthorizeAttribute"] = new("IAuthorizeData", "Microsoft.AspNetCore.Authorization"),
        ["IAudited"] = new("IAuditedObject", "Volo.Abp.Auditing"),
        ["IFullAudited"] = new("IFullAuditedObject", "Volo.Abp.Auditing"),
        ["ICreationAudited"] = new("ICreationAuditedObject", "Volo.Abp.Auditing"),
        ["IModificationAudited"] = new("IModificationAuditedObject", "Volo.Abp.Auditing"),
        ["IDeletionAudited"] = new("IDeletionAuditedObject", "Volo.Abp.Auditing"),
        ["IBackgroundJobManager"] = new("IBackgroundJobManager", "Volo.Abp.BackgroundJobs"),
        ["BackgroundJobManager"] = new("IBackgroundJobManager", "Volo.Abp.BackgroundJobs"),
        ["AbpValidationException"] = new("AbpValidationException", "Volo.Abp.Validation"),
        ["AbpAuthorizationException"] = new("AbpAuthorizationException", "Volo.Abp.Authorization"),
    };

    /// <summary>
    /// Boilerplate offers non-generic shortcuts that mean "int primary key". ABP.io's non-generic versions
    /// mean "no single primary key", so they are rewritten to the explicit &lt;int&gt; form.
    /// </summary>
    public static readonly HashSet<string> IntKeyShortcuts = new(StringComparer.Ordinal)
    {
        "Entity", "IEntity", "AggregateRoot",
        "CreationAuditedEntity", "AuditedEntity", "FullAuditedEntity",
        "CreationAuditedAggregateRoot", "AuditedAggregateRoot", "FullAuditedAggregateRoot",
        "EntityDto", "IEntityDto", "CreationAuditedEntityDto", "AuditedEntityDto", "FullAuditedEntityDto"
    };

    /// <summary>Generic types whose Boilerplate arity omits the int key: IRepository&lt;T&gt; == IRepository&lt;T, int&gt;.</summary>
    public static readonly IReadOnlyDictionary<string, int> AppendIntKeyWhenArity = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["IRepository"] = 1,
        ["EfCoreRepositoryBase"] = 2,
        ["EfCoreRepository"] = 2,
    };

    /// <summary>Audit property renames (Boilerplate → ABP.io).</summary>
    public static readonly IReadOnlyDictionary<string, string> MemberRenames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CreatorUserId"] = "CreatorId",
        ["LastModifierUserId"] = "LastModifierId",
        ["DeleterUserId"] = "DeleterId",
    };

    /// <summary>
    /// Module Zero constants (AbpUserBase.MaxUserNameLength...) replaced by their Boilerplate values, so validation
    /// attributes and column lengths stay exactly the same without depending on Module Zero.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ZeroConstants =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["AbpUserBase"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MaxUserNameLength"] = "256", ["MaxEmailAddressLength"] = "256", ["MaxNameLength"] = "64", ["MaxSurnameLength"] = "64",
                ["MaxAuthenticationSourceLength"] = "64", ["MaxPasswordLength"] = "128", ["MaxPlainPasswordLength"] = "32",
                ["MaxEmailConfirmationCodeLength"] = "328", ["MaxPasswordResetCodeLength"] = "328", ["MaxPhoneNumberLength"] = "32",
                ["AdminUserName"] = "\"admin\""
            },
            ["AbpTenantBase"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MaxTenancyNameLength"] = "64", ["MaxNameLength"] = "128", ["MaxConnectionStringLength"] = "1024",
                ["TenancyNameRegex"] = "\"^[a-zA-Z][a-zA-Z0-9_-]{1,}$\"", ["DefaultTenantName"] = "\"Default\""
            },
            ["AbpRoleBase"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MaxNameLength"] = "32", ["MaxDisplayNameLength"] = "64"
            },
            ["BackgroundJobInfo"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MaxJobTypeLength"] = "512", ["MaxJobArgsLength"] = "1048576", ["DefaultFirstWaitDuration"] = "60",
                ["DefaultTimeout"] = "172800", ["DefaultWaitFactor"] = "2.0"
            },
            ["AbpOrganizationUnit"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MaxDisplayNameLength"] = "128", ["MaxCodeLength"] = "95", ["MaxDepth"] = "16", ["CodeUnitLength"] = "5"
            },
        };

    /// <summary>ABP.io types that DTOs commonly use: namespace and the package that provides them.</summary>
    public static readonly IReadOnlyDictionary<string, (string Namespace, string Package)> AbpTypePackages =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["BackgroundJobPriority"] = ("Volo.Abp.BackgroundJobs", "Volo.Abp.BackgroundJobs.Abstractions"),
            ["DisableDateTimeNormalization"] = ("Volo.Abp.Timing", "Volo.Abp.Timing"),
            ["DisableDateTimeNormalizationAttribute"] = ("Volo.Abp.Timing", "Volo.Abp.Timing"),
            ["IClock"] = ("Volo.Abp.Timing", "Volo.Abp.Timing"),
            ["NameValue"] = ("Volo.Abp", "Volo.Abp.Core"),
            ["IHasExtraProperties"] = ("Volo.Abp.Data", "Volo.Abp.Data"),
            ["ExtraPropertyDictionary"] = ("Volo.Abp.Data", "Volo.Abp.Data"),
            ["IRemoteStreamContent"] = ("Volo.Abp.Content", "Volo.Abp.Ddd.Application.Contracts"),
            ["RemoteStreamContent"] = ("Volo.Abp.Content", "Volo.Abp.Ddd.Application.Contracts"),
        };

    /// <summary>Module Zero base types: no automatic conversion, the replacement is an ABP.io module.</summary>
    public static readonly IReadOnlyDictionary<string, string> ZeroTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AbpUser"] = "Volo.Abp.Identity.IdentityUser (extend it with ObjectExtensionManager / module entity extensions instead of inheriting)",
        ["AbpUserBase"] = "Volo.Abp.Identity.IdentityUser",
        ["AbpRole"] = "Volo.Abp.Identity.IdentityRole",
        ["AbpRoleBase"] = "Volo.Abp.Identity.IdentityRole",
        ["AbpTenant"] = "Volo.Abp.TenantManagement.Tenant",
        ["AbpTenantBase"] = "Volo.Abp.TenantManagement.Tenant",
        ["AbpUserManager"] = "Volo.Abp.Identity.IdentityUserManager",
        ["AbpRoleManager"] = "Volo.Abp.Identity.IdentityRoleManager",
        ["AbpTenantManager"] = "Volo.Abp.TenantManagement.ITenantManager",
        ["AbpLoginManager"] = "Microsoft.AspNetCore.Identity.SignInManager<IdentityUser> (or the Account module)",
        ["AbpSignInManager"] = "Volo.Abp.Identity.AspNetCore.AbpSignInManager",
        ["AbpUserStore"] = "Volo.Abp.Identity.IdentityUserStore",
        ["AbpRoleStore"] = "Volo.Abp.Identity.IdentityRoleStore",
        ["AbpUserClaimsPrincipalFactory"] = "Volo.Abp.Identity.AbpUserClaimsPrincipalFactory",
        ["AbpSecurityStampValidator"] = "Volo.Abp.Identity.AspNetCore.AbpSecurityStampValidator",
        ["PermissionChecker"] = "built-in IPermissionChecker (delete this class)",
        ["AbpEditionManager"] = "SaaS module (commercial) or a custom implementation",
        ["AbpFeatureValueStore"] = "Volo.Abp.FeatureManagement module (delete this class)",
        ["AbpZeroDbContext"] = "AbpDbContext<T> + builder.ConfigureIdentity()/ConfigureTenantManagement()...",
        ["AbpZeroTemplate"] = "-",
        ["LogInManager"] = "Account module",
    };
}
