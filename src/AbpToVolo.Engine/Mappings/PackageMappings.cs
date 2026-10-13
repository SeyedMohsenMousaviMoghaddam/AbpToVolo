using AbpToVolo.Core;

namespace AbpToVolo.Mappings;

public static class PackageMappings
{
    private static readonly string[] ZeroDomainPackages =
    [
        "Volo.Abp.Ddd.Domain",
        "Volo.Abp.Identity.Domain",
        "Volo.Abp.PermissionManagement.Domain.Identity",
        "Volo.Abp.TenantManagement.Domain",
        "Volo.Abp.SettingManagement.Domain",
        "Volo.Abp.FeatureManagement.Domain"
    ];

    private static readonly string[] ZeroEfCorePackages =
    [
        "Volo.Abp.EntityFrameworkCore.SqlServer",
        "Volo.Abp.Identity.EntityFrameworkCore",
        "Volo.Abp.PermissionManagement.EntityFrameworkCore",
        "Volo.Abp.TenantManagement.EntityFrameworkCore",
        "Volo.Abp.SettingManagement.EntityFrameworkCore",
        "Volo.Abp.FeatureManagement.EntityFrameworkCore"
    ];

    /// <summary>Boilerplate NuGet package → ABP.io packages. An empty array means "remove".</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Abp"] = ["Volo.Abp.Ddd.Domain"],
        ["Abp.AutoMapper"] = ["Volo.Abp.AutoMapper"],
        ["Abp.ZeroCore"] = ZeroDomainPackages,
        ["Abp.Zero.Common"] = ZeroDomainPackages,
        ["Abp.ZeroCore.EntityFrameworkCore"] = ZeroEfCorePackages,
        ["Abp.EntityFrameworkCore"] = ["Volo.Abp.EntityFrameworkCore.SqlServer"],
        ["Abp.EntityFrameworkCore.EFPlus"] = [],
        ["Abp.AspNetCore"] = ["Volo.Abp.AspNetCore.Mvc"],
        ["Abp.AspNetCore.SignalR"] = ["Volo.Abp.AspNetCore.SignalR"],
        ["Abp.AspNetCore.OData"] = [],
        ["Abp.AspNetCore.TestBase"] = ["Volo.Abp.AspNetCore.TestBase"],
        ["Abp.TestBase"] = ["Volo.Abp.TestBase", "Volo.Abp.Autofac"],
        ["Abp.Castle.Log4Net"] = ["Volo.Abp.Autofac"],
        ["Abp.Castle.NLog"] = ["Volo.Abp.Autofac"],
        ["Abp.HangFire"] = ["Volo.Abp.BackgroundJobs.HangFire"],
        ["Abp.HangFire.AspNetCore"] = ["Volo.Abp.BackgroundJobs.HangFire"],
        ["Abp.Quartz"] = ["Volo.Abp.BackgroundWorkers.Quartz"],
        ["Abp.QuartzCore"] = ["Volo.Abp.BackgroundWorkers.Quartz"],
        ["Abp.Zero.Ldap"] = [],
        ["Abp.RedisCache"] = ["Volo.Abp.Caching.StackExchangeRedis"],
        ["Abp.MailKit"] = ["Volo.Abp.MailKit"],
        ["Abp.Dapper"] = ["Volo.Abp.Dapper"],
        ["Abp.MongoDB"] = ["Volo.Abp.MongoDB"],
        ["Abp.FluentValidation"] = ["Volo.Abp.FluentValidation"],
        ["Abp.Web.Common"] = [],
        ["Abp.ZeroCore.IdentityServer4"] = [],
        ["Abp.ZeroCore.IdentityServer4.vNext"] = [],
        ["Abp.ZeroCore.IdentityServer4.EntityFrameworkCore"] = [],
        ["Abp.ZeroCore.OpenIddict"] = ["Volo.Abp.OpenIddict.Domain"],
        ["Abp.AspNetCore.OpenIddict"] = ["Volo.Abp.OpenIddict.AspNetCore"],
    };

    /// <summary>
    /// Namespaces used by the converted code → the package that provides them. Boilerplate's packages brought many of
    /// these transitively (e.g. Abp → Newtonsoft.Json, Abp.ZeroCore → BackgroundJobs); ABP.io packages are more granular.
    /// </summary>
    public static readonly (string Namespace, string Package, string? Version)[] NamespacePackages =
    [
        ("Volo.Abp.BackgroundJobs", "Volo.Abp.BackgroundJobs", null),
        ("Volo.Abp.BackgroundWorkers.Quartz", "Volo.Abp.BackgroundWorkers.Quartz", null),
        ("Quartz", "Volo.Abp.BackgroundWorkers.Quartz", null),
        ("Volo.Abp.BackgroundWorkers", "Volo.Abp.BackgroundWorkers", null),
        ("Volo.Abp.Application.Dtos", "Volo.Abp.Ddd.Application.Contracts", null),
        ("Volo.Abp.Application.Services", "Volo.Abp.Ddd.Application.Contracts", null),
        ("Volo.Abp.Caching.StackExchangeRedis", "Volo.Abp.Caching.StackExchangeRedis", null),
        ("Volo.Abp.Caching", "Volo.Abp.Caching", null),
        ("Volo.Abp.Emailing", "Volo.Abp.Emailing", null),
        ("Volo.Abp.MailKit", "Volo.Abp.MailKit", null),
        ("Volo.Abp.Sms", "Volo.Abp.Sms", null),
        ("Volo.Abp.BlobStoring", "Volo.Abp.BlobStoring", null),
        ("Volo.Abp.AspNetCore.SignalR", "Volo.Abp.AspNetCore.SignalR", null),
        ("Volo.Abp.AspNetCore.Mvc", "Volo.Abp.AspNetCore.Mvc", null),
        ("Volo.Abp.Http.Client", "Volo.Abp.Http.Client", null),
        ("Volo.Abp.Json", "Volo.Abp.Json", null),
        ("Volo.Abp.Timing", "Volo.Abp.Timing", null),
        ("Volo.Abp.Features", "Volo.Abp.Features", null),
        ("Volo.Abp.Threading", "Volo.Abp.Threading", null),
        ("Volo.Abp.ObjectExtending", "Volo.Abp.ObjectExtending", null),
        ("Volo.Abp.Identity", "Volo.Abp.Identity.Domain", null),
        ("Volo.Abp.TenantManagement", "Volo.Abp.TenantManagement.Domain", null),
        ("Volo.Abp.PermissionManagement", "Volo.Abp.PermissionManagement.Domain", null),
        ("Volo.Abp.SettingManagement", "Volo.Abp.SettingManagement.Domain", null),
        ("Volo.Abp.EventBus.Distributed", "Volo.Abp.EventBus", null),
        ("Newtonsoft.Json", "Newtonsoft.Json", "13.0.4"),
    ];

    /// <summary>Third-party packages that only exist because of Castle Windsor / Boilerplate infrastructure.</summary>
    public static bool IsObsoleteThirdParty(string package) =>
        package.StartsWith("Castle.", StringComparison.OrdinalIgnoreCase) ||
        package.Equals("Abp.Castle.Log4Net", StringComparison.OrdinalIgnoreCase);

    public static bool IsBoilerplatePackage(string package) =>
        package.Equals("Abp", StringComparison.OrdinalIgnoreCase) ||
        package.StartsWith("Abp.", StringComparison.OrdinalIgnoreCase);

    /// <summary>Packages every project of a given layer needs in an ABP.io solution.</summary>
    public static IEnumerable<string> LayerPackages(ProjectLayer layer, bool usesZero) => layer switch
    {
        ProjectLayer.DomainShared => ["Volo.Abp.Ddd.Domain.Shared", "Volo.Abp.Validation", "Volo.Abp.Localization"],
        ProjectLayer.Domain => ["Volo.Abp.Ddd.Domain", "Volo.Abp.Authorization.Abstractions", "Volo.Abp.Settings", "Volo.Abp.Localization"],
        ProjectLayer.ApplicationContracts => ["Volo.Abp.Ddd.Application.Contracts", "Volo.Abp.Authorization.Abstractions"],
        ProjectLayer.Application => ["Volo.Abp.Ddd.Application", "Volo.Abp.AutoMapper"],
        ProjectLayer.EntityFrameworkCore => usesZero ? ZeroEfCorePackages : ["Volo.Abp.EntityFrameworkCore.SqlServer"],
        ProjectLayer.HttpApi => ["Volo.Abp.AspNetCore.Mvc"],
        ProjectLayer.Host => ["Volo.Abp.Autofac", "Volo.Abp.AspNetCore.Mvc"],
        ProjectLayer.DbMigrator => ["Volo.Abp.Autofac"],
        ProjectLayer.Tests => ["Volo.Abp.TestBase", "Volo.Abp.Autofac"],
        _ => []
    };

    private static readonly string[] BumpablePrefixes =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore.Authentication.",
        "Microsoft.AspNetCore.SignalR.",
        "Microsoft.AspNetCore.Mvc.Testing",
        "Microsoft.AspNetCore.Mvc.NewtonsoftJson",
        "Microsoft.AspNetCore.TestHost",
        "Microsoft.AspNetCore.Identity.EntityFrameworkCore",
        "Microsoft.Extensions."
    ];

    /// <summary>Microsoft packages whose version follows the .NET runtime and must match the new target framework.</summary>
    public static bool IsRuntimeAlignedMicrosoftPackage(string package) =>
        BumpablePrefixes.Any(p => package.StartsWith(p, StringComparison.OrdinalIgnoreCase)) &&
        !package.StartsWith("Microsoft.Extensions.Caching.StackExchangeRedis", StringComparison.OrdinalIgnoreCase);
}
