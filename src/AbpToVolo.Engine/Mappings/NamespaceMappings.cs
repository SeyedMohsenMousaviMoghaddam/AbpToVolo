namespace AbpToVolo.Mappings;

/// <summary>
/// ASP.NET Boilerplate namespace → ABP Framework namespace(s).
/// An empty array means "the namespace has no counterpart and the using is simply dropped".
/// </summary>
public static class NamespaceMappings
{
    public static readonly IReadOnlyDictionary<string, string[]> Map = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["Abp"] = ["Volo.Abp"],
        ["Abp.UI"] = ["Volo.Abp"],
        ["Abp.Extensions"] = ["System"],
        ["Abp.Collections.Extensions"] = ["System.Collections.Generic", "System"],
        ["Abp.Linq.Extensions"] = ["System.Linq"],
        ["Abp.Linq"] = ["Volo.Abp.Linq"],
        ["Abp.Threading"] = ["Volo.Abp.Threading"],
        ["Abp.Threading.Extensions"] = ["Volo.Abp.Threading"],
        ["Abp.Threading.Timers"] = ["Volo.Abp.Threading"],
        ["Abp.Quartz.Configuration"] = ["Volo.Abp.BackgroundWorkers.Quartz", "Quartz"],
        ["Abp.Events.Bus.Exceptions"] = [],
        ["Abp.Authorization.Users"] = ["Volo.Abp.Identity"],
        ["Abp.Reflection.Extensions"] = [],
        ["Abp.Reflection"] = ["Volo.Abp.Reflection"],
        ["Abp.Json"] = ["Volo.Abp.Json"],
        ["Abp.Timing"] = ["Volo.Abp.Timing"],
        ["Abp.Data"] = ["Volo.Abp.Data"],
        ["Abp.Guids"] = ["Volo.Abp.Guids"],
        ["Abp.Specifications"] = ["Volo.Abp.Specifications"],

        ["Abp.Modules"] = ["Volo.Abp.Modularity"],
        ["Abp.Dependency"] = ["Volo.Abp.DependencyInjection"],

        ["Abp.Domain.Entities"] = ["Volo.Abp.Domain.Entities", "Volo.Abp"],
        ["Abp.Domain.Entities.Auditing"] = ["Volo.Abp.Domain.Entities.Auditing", "Volo.Abp.Auditing"],
        ["Abp.Domain.Entities.Caching"] = ["Volo.Abp.Domain.Entities.Caching"],
        ["Abp.Domain.Repositories"] = ["Volo.Abp.Domain.Repositories"],
        ["Abp.Domain.Services"] = ["Volo.Abp.Domain.Services"],
        ["Abp.Domain.Uow"] = ["Volo.Abp.Uow", "Volo.Abp.Data"],
        ["Abp.Domain.Values"] = ["Volo.Abp.Domain.Values"],
        ["Abp.Events.Bus"] = ["Volo.Abp.EventBus.Local"],
        ["Abp.Events.Bus.Handlers"] = ["Volo.Abp.EventBus", "Volo.Abp.EventBus.Local"],
        ["Abp.Events.Bus.Entities"] = ["Volo.Abp.Domain.Entities.Events"],

        ["Abp.Application.Services"] = ["Volo.Abp.Application.Services"],
        ["Abp.Application.Services.Dto"] = ["Volo.Abp.Application.Dtos"],
        ["Abp.ObjectMapping"] = ["Volo.Abp.ObjectMapping"],
        ["Abp.AutoMapper"] = ["AutoMapper", "Volo.Abp.AutoMapper"],

        ["Abp.Authorization"] = ["Microsoft.AspNetCore.Authorization", "Volo.Abp.Authorization", "Volo.Abp.Authorization.Permissions"],
        ["Abp.Authorization.Roles"] = [],
        ["Abp.Runtime.Session"] = ["Volo.Abp.Users", "Volo.Abp.MultiTenancy"],
        ["Abp.Runtime.Security"] = ["Volo.Abp.Security.Claims"],
        ["Abp.Runtime.Caching"] = ["Volo.Abp.Caching", "Microsoft.Extensions.Caching.Distributed"],
        ["Abp.Runtime.Validation"] = ["Volo.Abp.Validation"],
        ["Abp.Runtime.Validation.Interception"] = ["Volo.Abp.Validation"],
        ["Abp.Configuration"] = ["Volo.Abp.Settings"],
        ["Abp.Configuration.Startup"] = [],
        ["Abp.MultiTenancy"] = ["Volo.Abp.MultiTenancy"],
        ["Abp.Auditing"] = ["Volo.Abp.Auditing"],
        ["Abp.EntityHistory"] = ["Volo.Abp.Auditing"],
        ["Abp.BackgroundJobs"] = ["Volo.Abp.BackgroundJobs"],
        ["Abp.Threading.BackgroundWorkers"] = ["Volo.Abp.BackgroundWorkers"],
        ["Abp.Net.Mail"] = ["Volo.Abp.Emailing"],
        ["Abp.Localization"] = ["Volo.Abp.Localization", "Microsoft.Extensions.Localization"],
        ["Abp.Localization.Dictionaries"] = [],
        ["Abp.Localization.Dictionaries.Xml"] = [],
        ["Abp.Localization.Sources"] = [],
        ["Abp.Features"] = ["Volo.Abp.Features"],
        ["Abp.Application.Features"] = ["Volo.Abp.Features"],
        ["Abp.Application.Navigation"] = ["Volo.Abp.UI.Navigation"],
        // Abp.Notifications, Abp.RealTime, Abp.Webhooks are features without an ABP.io counterpart:
        // they are deliberately not listed so that the file gets a TODO instead of a silent removal.

        ["Abp.EntityFrameworkCore"] = ["Volo.Abp.EntityFrameworkCore"],
        ["Abp.EntityFrameworkCore.Configuration"] = ["Volo.Abp.EntityFrameworkCore"],
        ["Abp.EntityFrameworkCore.Repositories"] = ["Volo.Abp.Domain.Repositories.EntityFrameworkCore"],
        ["Abp.EntityFrameworkCore.Uow"] = ["Volo.Abp.Uow"],
        ["Abp.EntityFrameworkCore.Extensions"] = ["Volo.Abp.EntityFrameworkCore"],
        ["Abp.Zero.EntityFrameworkCore"] = ["Volo.Abp.EntityFrameworkCore"],
        ["Abp.IdentityFramework"] = ["Volo.Abp.Identity"],
        ["Abp.Zero"] = [],
        ["Abp.Zero.Configuration"] = [],

        ["Abp.AspNetCore"] = ["Volo.Abp.AspNetCore"],
        ["Abp.AspNetCore.Mvc.Controllers"] = ["Volo.Abp.AspNetCore.Mvc"],
        // ActionDescriptor.IsControllerAction() / GetMethodInfo(), ActionResultHelper: same names in Volo.Abp.AspNetCore.Mvc.
        ["Abp.AspNetCore.Mvc.Extensions"] = ["Volo.Abp.AspNetCore.Mvc", "Microsoft.AspNetCore.Mvc.Abstractions"],
        ["Abp.AspNetCore.Mvc.Results"] = ["Volo.Abp.AspNetCore.Mvc"],
        ["Abp.AspNetCore.Mvc.Authorization"] = ["Microsoft.AspNetCore.Authorization"],
        ["Abp.AspNetCore.Mvc.Antiforgery"] = ["Volo.Abp.AspNetCore.Mvc.AntiForgery"],
        ["Abp.AspNetCore.Configuration"] = ["Volo.Abp.AspNetCore.Mvc"],
        ["Abp.AspNetCore.SignalR"] = ["Volo.Abp.AspNetCore.SignalR"],
        ["Abp.AspNetCore.SignalR.Hubs"] = ["Volo.Abp.AspNetCore.SignalR"],
        ["Abp.AspNetCore.Dependency"] = ["Volo.Abp.Autofac"],
        ["Abp.AspNetCore.TestBase"] = ["Volo.Abp.AspNetCore.TestBase"],
        ["Abp.Web.Models"] = [],
        ["Abp.Web.Models.AbpUserConfiguration"] = [],
        ["Abp.Castle.Logging.Log4Net"] = [],
        ["Abp.Castle.Logging.Log4Net.Extensions"] = [],

        ["Abp.TestBase"] = ["Volo.Abp.Testing"],
        ["Abp.Hangfire"] = ["Volo.Abp.BackgroundJobs.Hangfire"],
        ["Abp.Hangfire.Configuration"] = ["Volo.Abp.BackgroundJobs.Hangfire"],
        ["Abp.Quartz"] = ["Volo.Abp.BackgroundWorkers.Quartz", "Quartz"],
        ["Abp.Runtime.Caching.Redis"] = ["Volo.Abp.Caching.StackExchangeRedis"],
        ["Abp.MailKit"] = ["Volo.Abp.MailKit"],
        ["Abp.Dapper"] = ["Volo.Abp.Dapper"],
        ["Abp.Dapper.Repositories"] = ["Volo.Abp.Domain.Repositories.Dapper"],
        ["Abp.MongoDb"] = ["Volo.Abp.MongoDB"],
        ["Abp.FluentValidation"] = ["Volo.Abp.FluentValidation"],
    };

    /// <summary>Castle / Boilerplate-only namespaces that are dropped without replacement.</summary>
    public static bool IsDroppedThirdParty(string ns) =>
        ns == "Castle" || ns.StartsWith("Castle.", StringComparison.Ordinal);

    public static bool IsBoilerplateNamespace(string ns) =>
        ns == "Abp" || ns.StartsWith("Abp.", StringComparison.Ordinal);
}
