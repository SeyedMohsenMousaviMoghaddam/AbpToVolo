using AbpToVolo.Core;

namespace AbpToVolo.Mappings;

public sealed record AbpPackageModule(string Package, string Module, string Namespace);

/// <summary>
/// The ABP.io application modules of the layered "app" template that replace Module Zero, per layer
/// (names verified against the Volo.Abp 10.x packages).
/// </summary>
public static class AbpModuleCatalog
{
    private static AbpPackageModule P(string package, string module, string ns) => new(package, module, ns);

    public static readonly IReadOnlyDictionary<ProjectLayer, AbpPackageModule[]> IdentityModules = new Dictionary<ProjectLayer, AbpPackageModule[]>
    {
        [ProjectLayer.DomainShared] =
        [
            P("Volo.Abp.Identity.Domain.Shared", "AbpIdentityDomainSharedModule", "Volo.Abp.Identity"),
            P("Volo.Abp.TenantManagement.Domain.Shared", "AbpTenantManagementDomainSharedModule", "Volo.Abp.TenantManagement"),
            P("Volo.Abp.PermissionManagement.Domain.Shared", "AbpPermissionManagementDomainSharedModule", "Volo.Abp.PermissionManagement"),
            P("Volo.Abp.SettingManagement.Domain.Shared", "AbpSettingManagementDomainSharedModule", "Volo.Abp.SettingManagement"),
            P("Volo.Abp.FeatureManagement.Domain.Shared", "AbpFeatureManagementDomainSharedModule", "Volo.Abp.FeatureManagement"),
            P("Volo.Abp.OpenIddict.Domain.Shared", "AbpOpenIddictDomainSharedModule", "Volo.Abp.OpenIddict"),
        ],
        [ProjectLayer.Domain] =
        [
            P("Volo.Abp.Identity.Domain", "AbpIdentityDomainModule", "Volo.Abp.Identity"),
            P("Volo.Abp.TenantManagement.Domain", "AbpTenantManagementDomainModule", "Volo.Abp.TenantManagement"),
            P("Volo.Abp.PermissionManagement.Domain.Identity", "AbpPermissionManagementDomainIdentityModule", "Volo.Abp.PermissionManagement.Identity"),
            P("Volo.Abp.PermissionManagement.Domain.OpenIddict", "AbpPermissionManagementDomainOpenIddictModule", "Volo.Abp.PermissionManagement.OpenIddict"),
            P("Volo.Abp.SettingManagement.Domain", "AbpSettingManagementDomainModule", "Volo.Abp.SettingManagement"),
            P("Volo.Abp.FeatureManagement.Domain", "AbpFeatureManagementDomainModule", "Volo.Abp.FeatureManagement"),
            P("Volo.Abp.OpenIddict.Domain", "AbpOpenIddictDomainModule", "Volo.Abp.OpenIddict"),
        ],
        [ProjectLayer.ApplicationContracts] =
        [
            P("Volo.Abp.Identity.Application.Contracts", "AbpIdentityApplicationContractsModule", "Volo.Abp.Identity"),
            P("Volo.Abp.TenantManagement.Application.Contracts", "AbpTenantManagementApplicationContractsModule", "Volo.Abp.TenantManagement"),
            P("Volo.Abp.PermissionManagement.Application.Contracts", "AbpPermissionManagementApplicationContractsModule", "Volo.Abp.PermissionManagement"),
            P("Volo.Abp.SettingManagement.Application.Contracts", "AbpSettingManagementApplicationContractsModule", "Volo.Abp.SettingManagement"),
            P("Volo.Abp.FeatureManagement.Application.Contracts", "AbpFeatureManagementApplicationContractsModule", "Volo.Abp.FeatureManagement"),
            P("Volo.Abp.Account.Application.Contracts", "AbpAccountApplicationContractsModule", "Volo.Abp.Account"),
        ],
        [ProjectLayer.Application] =
        [
            P("Volo.Abp.Identity.Application", "AbpIdentityApplicationModule", "Volo.Abp.Identity"),
            P("Volo.Abp.TenantManagement.Application", "AbpTenantManagementApplicationModule", "Volo.Abp.TenantManagement"),
            P("Volo.Abp.PermissionManagement.Application", "AbpPermissionManagementApplicationModule", "Volo.Abp.PermissionManagement"),
            P("Volo.Abp.SettingManagement.Application", "AbpSettingManagementApplicationModule", "Volo.Abp.SettingManagement"),
            P("Volo.Abp.FeatureManagement.Application", "AbpFeatureManagementApplicationModule", "Volo.Abp.FeatureManagement"),
            P("Volo.Abp.Account.Application", "AbpAccountApplicationModule", "Volo.Abp.Account"),
        ],
        [ProjectLayer.EntityFrameworkCore] =
        [
            P("Volo.Abp.Identity.EntityFrameworkCore", "AbpIdentityEntityFrameworkCoreModule", "Volo.Abp.Identity.EntityFrameworkCore"),
            P("Volo.Abp.TenantManagement.EntityFrameworkCore", "AbpTenantManagementEntityFrameworkCoreModule", "Volo.Abp.TenantManagement.EntityFrameworkCore"),
            P("Volo.Abp.PermissionManagement.EntityFrameworkCore", "AbpPermissionManagementEntityFrameworkCoreModule", "Volo.Abp.PermissionManagement.EntityFrameworkCore"),
            P("Volo.Abp.SettingManagement.EntityFrameworkCore", "AbpSettingManagementEntityFrameworkCoreModule", "Volo.Abp.SettingManagement.EntityFrameworkCore"),
            P("Volo.Abp.FeatureManagement.EntityFrameworkCore", "AbpFeatureManagementEntityFrameworkCoreModule", "Volo.Abp.FeatureManagement.EntityFrameworkCore"),
            P("Volo.Abp.OpenIddict.EntityFrameworkCore", "AbpOpenIddictEntityFrameworkCoreModule", "Volo.Abp.OpenIddict.EntityFrameworkCore"),
        ],
        [ProjectLayer.HttpApi] =
        [
            P("Volo.Abp.Identity.HttpApi", "AbpIdentityHttpApiModule", "Volo.Abp.Identity"),
            P("Volo.Abp.TenantManagement.HttpApi", "AbpTenantManagementHttpApiModule", "Volo.Abp.TenantManagement"),
            P("Volo.Abp.PermissionManagement.HttpApi", "AbpPermissionManagementHttpApiModule", "Volo.Abp.PermissionManagement.HttpApi"),
            P("Volo.Abp.SettingManagement.HttpApi", "AbpSettingManagementHttpApiModule", "Volo.Abp.SettingManagement"),
            P("Volo.Abp.FeatureManagement.HttpApi", "AbpFeatureManagementHttpApiModule", "Volo.Abp.FeatureManagement"),
            P("Volo.Abp.Account.HttpApi", "AbpAccountHttpApiModule", "Volo.Abp.Account"),
        ],
    };

    /// <summary>Modules of the HttpApi.Host of the ABP.io template (OpenIddict auth server in the host).</summary>
    public static readonly AbpPackageModule[] HostModules =
    [
        P("Volo.Abp.Autofac", "AbpAutofacModule", "Volo.Abp.Autofac"),
        P("Volo.Abp.AspNetCore.MultiTenancy", "AbpAspNetCoreMultiTenancyModule", "Volo.Abp.AspNetCore.MultiTenancy"),
        P("Volo.Abp.AspNetCore.Serilog", "AbpAspNetCoreSerilogModule", "Volo.Abp.AspNetCore.Serilog"),
        P("Volo.Abp.Swashbuckle", "AbpSwashbuckleModule", "Volo.Abp.Swashbuckle"),
    ];

    public static readonly AbpPackageModule[] HostIdentityModules =
    [
        P("Volo.Abp.Account.Web.OpenIddict", "AbpAccountWebOpenIddictModule", "Volo.Abp.Account.Web"),
        P("Volo.Abp.AspNetCore.Mvc.UI.Theme.Basic", "AbpAspNetCoreMvcUiBasicThemeModule", "Volo.Abp.AspNetCore.Mvc.UI.Theme.Basic"),
    ];
}
