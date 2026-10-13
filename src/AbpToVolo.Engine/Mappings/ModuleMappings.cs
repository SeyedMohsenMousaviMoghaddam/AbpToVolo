namespace AbpToVolo.Mappings;

public sealed record AbpModuleRef(string TypeName, string Namespace);

/// <summary>Boilerplate module classes used in [DependsOn] → ABP.io module classes.</summary>
public static class ModuleMappings
{
    private static AbpModuleRef M(string type, string ns) => new(type, ns);

    public static readonly AbpModuleRef DddDomain = M("AbpDddDomainModule", "Volo.Abp.Domain");
    public static readonly AbpModuleRef DddApplication = M("AbpDddApplicationModule", "Volo.Abp.Application");
    public static readonly AbpModuleRef Autofac = M("AbpAutofacModule", "Volo.Abp.Autofac");
    public static readonly AbpModuleRef AutoMapper = M("AbpAutoMapperModule", "Volo.Abp.AutoMapper");

    private static readonly AbpModuleRef[] ZeroDomain =
    [
        DddDomain,
        M("AbpIdentityDomainModule", "Volo.Abp.Identity"),
        M("AbpPermissionManagementDomainIdentityModule", "Volo.Abp.PermissionManagement.Identity"),
        M("AbpTenantManagementDomainModule", "Volo.Abp.TenantManagement"),
        M("AbpSettingManagementDomainModule", "Volo.Abp.SettingManagement"),
        M("AbpFeatureManagementDomainModule", "Volo.Abp.FeatureManagement")
    ];

    private static readonly AbpModuleRef[] ZeroEfCore =
    [
        M("AbpEntityFrameworkCoreSqlServerModule", "Volo.Abp.EntityFrameworkCore.SqlServer"),
        M("AbpIdentityEntityFrameworkCoreModule", "Volo.Abp.Identity.EntityFrameworkCore"),
        M("AbpPermissionManagementEntityFrameworkCoreModule", "Volo.Abp.PermissionManagement.EntityFrameworkCore"),
        M("AbpTenantManagementEntityFrameworkCoreModule", "Volo.Abp.TenantManagement.EntityFrameworkCore"),
        M("AbpSettingManagementEntityFrameworkCoreModule", "Volo.Abp.SettingManagement.EntityFrameworkCore"),
        M("AbpFeatureManagementEntityFrameworkCoreModule", "Volo.Abp.FeatureManagement.EntityFrameworkCore")
    ];

    /// <summary>An empty array means the dependency is dropped (ABP.io loads the equivalent automatically).</summary>
    public static readonly IReadOnlyDictionary<string, AbpModuleRef[]> Map = new Dictionary<string, AbpModuleRef[]>(StringComparer.Ordinal)
    {
        ["AbpKernelModule"] = [],
        ["AbpZeroCoreModule"] = ZeroDomain,
        ["AbpZeroCommonModule"] = ZeroDomain,
        ["AbpAutoMapperModule"] = [AutoMapper],
        ["AbpEntityFrameworkCoreModule"] = [M("AbpEntityFrameworkCoreSqlServerModule", "Volo.Abp.EntityFrameworkCore.SqlServer")],
        ["AbpZeroCoreEntityFrameworkCoreModule"] = ZeroEfCore,
        ["AbpAspNetCoreModule"] = [M("AbpAspNetCoreMvcModule", "Volo.Abp.AspNetCore.Mvc")],
        ["AbpAspNetCoreSignalRModule"] = [M("AbpAspNetCoreSignalRModule", "Volo.Abp.AspNetCore.SignalR")],
        ["AbpCastleLog4NetModule"] = [Autofac],
        ["AbpTestBaseModule"] = [M("AbpTestBaseModule", "Volo.Abp.Testing"), Autofac],
        ["AbpAspNetCoreTestBaseModule"] = [M("AbpAspNetCoreTestBaseModule", "Volo.Abp.AspNetCore.TestBase")],
        ["AbpHangfireAspNetCoreModule"] = [M("AbpBackgroundJobsHangfireModule", "Volo.Abp.BackgroundJobs.Hangfire")],
        ["AbpHangfireModule"] = [M("AbpBackgroundJobsHangfireModule", "Volo.Abp.BackgroundJobs.Hangfire")],
        ["AbpQuartzModule"] = [M("AbpBackgroundWorkersQuartzModule", "Volo.Abp.BackgroundWorkers.Quartz")],
        ["AbpRedisCacheModule"] = [M("AbpCachingStackExchangeRedisModule", "Volo.Abp.Caching.StackExchangeRedis")],
        ["AbpMailKitModule"] = [M("AbpMailKitModule", "Volo.Abp.MailKit")],
        ["AbpDapperModule"] = [M("AbpDapperModule", "Volo.Abp.Dapper")],
        ["AbpMongoDbModule"] = [M("AbpMongoDbModule", "Volo.Abp.MongoDB")],
        ["AbpFluentValidationModule"] = [M("AbpFluentValidationModule", "Volo.Abp.FluentValidation")],
        ["AbpWebCommonModule"] = [],
        ["AbpZeroCoreIdentityServerEntityFrameworkCoreModule"] = [],
    };
}
