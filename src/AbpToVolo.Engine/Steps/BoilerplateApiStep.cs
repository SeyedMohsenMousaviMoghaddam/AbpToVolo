using System.Text.RegularExpressions;
using AbpToVolo.CodeMigration;
using AbpToVolo.Core;

namespace AbpToVolo.Steps;

/// <summary>
/// --api-style boilerplate (default): the converted solution keeps the HTTP API of the source, so the existing front end
/// works without changes:
///   - application services: /api/services/app/{Service}/{Action} (Boilerplate route; ABP.io: /api/app/{service}/{id}),
///     through an IConventionalRouteBuilder replacement — the HTTP verbs follow the same method-name prefixes;
///   - results in the AjaxResponse envelope { result, targetUrl, success, error, unAuthorizedRequest, __abp }, errors
///     (RemoteServiceErrorResponse of ABP.io) and 401/403 answers too;
///   - [DontWrapResult] / [WrapResult] keep working (compatibility attributes in Domain.Shared).
/// The ABP.io module endpoints (/api/abp/..., /api/identity/..., /connect/token) are not changed.
/// </summary>
public sealed class BoilerplateApiStep : IMigrationStep
{
    public string Name => "Boilerplate HTTP API compatibility";

    public bool IsEnabled(MigrationOptions options) => options.ApiStyle == ApiStyleMode.Boilerplate;

    public void Execute(MigrationContext context)
    {
        var httpApi = context.FindProject(ProjectLayer.HttpApi);
        var shared = context.FindProject(ProjectLayer.DomainShared) ?? context.FindProject(ProjectLayer.ApplicationContracts) ?? context.FindProject(ProjectLayer.Domain);
        if (httpApi is null || shared is null)
        {
            context.Report.Warning(Name, "No HttpApi / Domain.Shared project: the Boilerplate routes and the AjaxResponse envelope are not generated.");
            return;
        }

        var ns = context.SolutionName is { } solution && httpApi.RootNamespace.StartsWith(solution, StringComparison.Ordinal) ? solution : httpApi.RootNamespace;
        var sources = context.Projects.Where(p => p.Layer != ProjectLayer.Tests)
            .SelectMany(p => FileSystem.EnumerateFiles(p.Directory, "*.cs"))
            .Where(f => !context.IsExcluded(f) && !f.Contains(Path.DirectorySeparatorChar + CompatShimStep.ShimFolder + Path.DirectorySeparatorChar))
            .Select(File.ReadAllText)
            .ToList();

        // Models and attributes: visible to every layer (the application services use the attributes).
        if (!sources.Any(s => Regex.IsMatch(s, @"\bclass AjaxResponse\b|\bclass DontWrapResultAttribute\b")))
        {
            var models = Path.Combine(shared.Directory, CompatShimStep.ShimFolder, "BoilerplateApiModels.cs");
            FileSystem.WriteText(models, ModelsCode(ns));
            context.Report.Change(Name, "AjaxResponse, ErrorInfo, [WrapResult] / [DontWrapResult] (Boilerplate API models)", context.Relative(models));
        }

        var mvc = Path.Combine(httpApi.Directory, CompatShimStep.ShimFolder, "BoilerplateApi.cs");
        FileSystem.WriteText(mvc, MvcCode(ns));
        var wired = Wire(context, httpApi, ProjectLayer.HttpApi, $"global::{ns}.BoilerplateApi.ConfigureServices(context.Services);");
        AddTenantIdResolver(context, ns);

        context.Report.Change(Name, "Application services keep the Boilerplate routes /api/services/app/{Service}/{Action}; results, errors and 401/403 " +
                                    "answers keep the AjaxResponse envelope ([DontWrapResult] respected)", context.Relative(mvc));
        if (!wired)
        {
            context.Report.Todo(Name, $"Call {ns}.BoilerplateApi.ConfigureServices(context.Services) in ConfigureServices of the HttpApi module.", context.Relative(mvc));
        }

        context.Report.Info(Name, "To move to the ABP.io API (/api/app/..., results not wrapped) later: convert with --api-style abp, or remove " +
                                  "BoilerplateApi.ConfigureServices and BoilerplateConventionalRouteBuilder, then update the front end.");
    }

    /// <summary>
    /// Boilerplate clients select the tenant with the "Abp.TenantId" header/cookie (the int id of the source). With
    /// --id-type source the tenants keep that id as LegacyId: a tenant resolver in the host maps it to the ABP.io tenant.
    /// </summary>
    private void AddTenantIdResolver(MigrationContext context, string ns)
    {
        if (context.Options.IdType != IdTypeMode.Source || !context.UsesIdentityModules) return;
        if (context.FindProject(ProjectLayer.Host) is not { } host) return;

        var file = Path.Combine(host.Directory, CompatShimStep.ShimFolder, "BoilerplateTenantIdResolveContributor.cs");
        FileSystem.WriteText(file, TenantResolverCode(ns));
        var wired = Wire(context, host, ProjectLayer.Host,
            $"Configure<global::Volo.Abp.MultiTenancy.AbpTenantResolveOptions>(options => options.TenantResolvers.Insert(1, new global::{ns}.BoilerplateTenantIdResolveContributor()));");
        context.Report.Change(Name, "Tenant selected by the Boilerplate \"Abp.TenantId\" header / cookie (int id of the source → tenant with that LegacyId)", context.Relative(file));
        if (!wired)
        {
            context.Report.Todo(Name, "Add BoilerplateTenantIdResolveContributor to AbpTenantResolveOptions.TenantResolvers in the host module.", context.Relative(file));
        }
    }

    internal static string TenantResolverCode(string ns) => $$"""
        // AbpToVolo: Boilerplate clients send the tenant as the "Abp.TenantId" header (or cookie) with the int id of the
        // source. The ABP.io tenants keep that id as LegacyId (--id-type source): this resolver maps it to the tenant.
        using System;
        using System.Collections.Concurrent;
        using System.Linq;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Http;
        using Microsoft.Extensions.DependencyInjection;
        using Volo.Abp.AspNetCore.MultiTenancy;
        using Volo.Abp.MultiTenancy;
        using Volo.Abp.Uow;

        namespace {{ns}};

        public class BoilerplateTenantIdResolveContributor : HttpTenantResolveContributorBase
        {
            public const string HeaderName = "Abp.TenantId";
            public const string ContributorName = "BoilerplateTenantId";

            private static readonly ConcurrentDictionary<int, Guid> Tenants = new();

            public override string Name => ContributorName;

            protected override async Task<string?> GetTenantIdOrNameFromHttpContextOrNullAsync(ITenantResolveContext context, HttpContext httpContext)
            {
                var value = httpContext.Request.Headers[HeaderName].FirstOrDefault() ?? httpContext.Request.Cookies[HeaderName];
                if (!int.TryParse(value, out var legacyId)) return null;
                if (Tenants.TryGetValue(legacyId, out var known)) return known.ToString();

                using var uow = context.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true, isTransactional: false);
                var tenant = await context.ServiceProvider.GetRequiredService<ILegacyIdLookup>().FindTenantAsync(legacyId);
                await uow.CompleteAsync();
                if (tenant is null) return null;

                Tenants[legacyId] = tenant.Id;
                return tenant.Id.ToString();
            }
        }

        """;

    private static bool Wire(MigrationContext context, ProjectInfo project, ProjectLayer layer, string statement)
    {
        if (AbpModulesStep.FindModuleFile(project, AbpModulesStep.ModuleNameFilters[layer]) is not { } file) return false;
        var text = File.ReadAllText(file);
        var updated = ModuleMethodEditor.AddStatement(text, AbpModulesStep.ModuleNameFilters[layer], "ConfigureServices", "ServiceConfigurationContext context", statement);
        if (updated is null) return false;
        if (updated != text) FileSystem.WriteText(file, updated);
        return true;
    }

    internal static string ModelsCode(string ns) => $$"""
        // AbpToVolo: the response models of the ASP.NET Boilerplate HTTP API (Abp.Web.Models), so the clients of the
        // source keep working (see BoilerplateApi in the HttpApi project). Generated by --api-style boilerplate.
        using System;
        using System.Text.Json.Serialization;

        namespace {{ns}};

        /// <summary>Wraps the results of an action / application service in <see cref="AjaxResponse"/> (Boilerplate default).</summary>
        [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Method)]
        public class WrapResultAttribute : Attribute
        {
            public bool WrapOnSuccess { get; set; }

            public bool WrapOnError { get; set; }

            public bool LogError { get; set; }

            public WrapResultAttribute(bool wrapOnSuccess = true, bool wrapOnError = true)
            {
                WrapOnSuccess = wrapOnSuccess;
                WrapOnError = wrapOnError;
                LogError = true;
            }
        }

        /// <summary>The results (and errors) are returned as they are, without <see cref="AjaxResponse"/>.</summary>
        public class DontWrapResultAttribute : WrapResultAttribute
        {
            public DontWrapResultAttribute()
                : base(false, false)
            {
            }
        }

        [Serializable]
        public class ValidationErrorInfo
        {
            public string? Message { get; set; }

            public string[]? Members { get; set; }

            public ValidationErrorInfo()
            {
            }

            public ValidationErrorInfo(string? message, params string[]? members)
            {
                Message = message;
                Members = members;
            }
        }

        [Serializable]
        public class ErrorInfo
        {
            public int Code { get; set; }

            public string? Message { get; set; }

            public string? Details { get; set; }

            public ValidationErrorInfo[]? ValidationErrors { get; set; }

            public ErrorInfo()
            {
            }

            public ErrorInfo(string? message, string? details = null, int code = 0)
            {
                Message = message;
                Details = details;
                Code = code;
            }
        }

        public abstract class AjaxResponseBase
        {
            public string? TargetUrl { get; set; }

            public bool Success { get; set; }

            public ErrorInfo? Error { get; set; }

            public bool UnAuthorizedRequest { get; set; }

            [JsonPropertyName("__abp")]
            public bool __abp { get; } = true;
        }

        [Serializable]
        public class AjaxResponse<TResult> : AjaxResponseBase
        {
            public TResult? Result { get; set; }

            public AjaxResponse()
            {
                Success = true;
            }

            public AjaxResponse(TResult? result)
            {
                Result = result;
                Success = true;
            }

            public AjaxResponse(bool success)
            {
                Success = success;
            }

            public AjaxResponse(ErrorInfo error, bool unAuthorizedRequest = false)
            {
                Error = error;
                UnAuthorizedRequest = unAuthorizedRequest;
                Success = false;
            }
        }

        [Serializable]
        public class AjaxResponse : AjaxResponse<object>
        {
            public AjaxResponse()
            {
            }

            public AjaxResponse(bool success)
                : base(success)
            {
            }

            public AjaxResponse(object? result)
                : base(result)
            {
            }

            public AjaxResponse(ErrorInfo error, bool unAuthorizedRequest = false)
                : base(error, unAuthorizedRequest)
            {
            }
        }

        """;

    internal static string MvcCode(string ns) => $$"""
        // AbpToVolo: the HTTP API of ASP.NET Boilerplate on ABP.io (generated by --api-style boilerplate):
        //   - application services: /api/services/app/{Service}/{Action} (ABP.io: /api/app/{service}/{id});
        //   - results, errors and 401/403 answers in the AjaxResponse envelope, unless [DontWrapResult] / [WrapResult(false, ...)].
        // Only the solution's own services and controllers are affected: the ABP.io module endpoints stay as they are.
        using System;
        using System.Linq;
        using System.Reflection;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Authorization;
        using Microsoft.AspNetCore.Authorization.Policy;
        using Microsoft.AspNetCore.Http;
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.AspNetCore.Mvc.ApplicationModels;
        using Microsoft.AspNetCore.Mvc.Controllers;
        using Microsoft.AspNetCore.Mvc.Filters;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.DependencyInjection.Extensions;
        using Microsoft.Extensions.Options;
        using Volo.Abp.AspNetCore.Mvc.Conventions;
        using Volo.Abp.DependencyInjection;
        using Volo.Abp.Http;

        namespace {{ns}};

        public static class BoilerplateApi
        {
            public static void ConfigureServices(IServiceCollection services)
            {
                services.Configure<MvcOptions>(options => options.Filters.AddService<BoilerplateResultWrapFilter>());
                services.Replace(ServiceDescriptor.Singleton<IAuthorizationMiddlewareResultHandler, BoilerplateAuthorizationResultHandler>());
            }

            /// <summary>Services and controllers of ABP.io, ASP.NET Core and libraries keep their own API.</summary>
            public static bool IsFrameworkType(Type type)
            {
                var assembly = type.Assembly.GetName().Name ?? "";
                return assembly.StartsWith("Volo.", StringComparison.Ordinal) || assembly.StartsWith("Microsoft.", StringComparison.Ordinal) ||
                       assembly.StartsWith("Swashbuckle", StringComparison.Ordinal) || assembly.StartsWith("OpenIddict", StringComparison.Ordinal);
            }

            /// <summary>Boilerplate service name: the class name without AppService / ApplicationService.</summary>
            public static string ServiceName(Type type)
            {
                var name = type.Name;
                var tick = name.IndexOf('`');
                if (tick >= 0) name = name[..tick];
                foreach (var postfix in new[] { "ApplicationService", "AppService" })
                {
                    if (name.Length > postfix.Length && name.EndsWith(postfix, StringComparison.Ordinal)) return name[..^postfix.Length];
                }

                return name;
            }

            /// <summary>[WrapResult] of the method, of an interface method it implements, of the class or of its interfaces.</summary>
            public static WrapResultAttribute GetWrapResult(MethodInfo? method, Type type)
            {
                if (method?.GetCustomAttribute<WrapResultAttribute>(true) is { } onMethod) return onMethod;
                if (method is not null)
                {
                    foreach (var @interface in type.GetInterfaces())
                    {
                        var map = type.GetInterfaceMap(@interface);
                        var index = Array.IndexOf(map.TargetMethods, method);
                        if (index >= 0 && map.InterfaceMethods[index].GetCustomAttribute<WrapResultAttribute>(true) is { } onInterfaceMethod) return onInterfaceMethod;
                    }
                }

                return type.GetCustomAttribute<WrapResultAttribute>(true)
                       ?? type.GetInterfaces().Select(i => i.GetCustomAttribute<WrapResultAttribute>(true)).FirstOrDefault(a => a is not null)
                       ?? new WrapResultAttribute();
            }

            public static ErrorInfo ToErrorInfo(RemoteServiceErrorInfo? error) =>
                new(error?.Message, error?.Details, int.TryParse(error?.Code, out var code) ? code : 0)
                {
                    ValidationErrors = error?.ValidationErrors?.Select(v => new ValidationErrorInfo(v.Message, v.Members)).ToArray()
                };
        }

        /// <summary>Boilerplate routes for the application services: api/services/{module}/{Service}/{Action}.</summary>
        [Dependency(ReplaceServices = true)]
        [ExposeServices(typeof(IConventionalRouteBuilder))]
        public class BoilerplateConventionalRouteBuilder : ConventionalRouteBuilder
        {
            public BoilerplateConventionalRouteBuilder(IOptions<AbpConventionalControllerOptions> options)
                : base(options)
            {
            }

            public override string Build(string rootPath, string controllerName, ActionModel action, string httpMethod, ConventionalControllerSetting? configuration)
            {
                var type = action.Controller.ControllerType.AsType();
                if (BoilerplateApi.IsFrameworkType(type)) return base.Build(rootPath, controllerName, action, httpMethod, configuration);
                return $"api/services/{rootPath}/{BoilerplateApi.ServiceName(type)}/{ActionName(action, type)}";
            }

            /// <summary>The CRUD method GetListAsync of ABP.io was GetAll in the Boilerplate CRUD application services.</summary>
            private static string ActionName(ActionModel action, Type type) =>
                action.ActionMethod.Name == "GetListAsync" && type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition().Name.StartsWith("IReadOnlyAppService", StringComparison.Ordinal))
                    ? "GetAll"
                    : action.ActionName;
        }

        /// <summary>Results and errors of the solution's actions → AjaxResponse (also runs for the results of exception filters).</summary>
        public class BoilerplateResultWrapFilter : IAsyncAlwaysRunResultFilter, ITransientDependency
        {
            public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
            {
                if (context.ActionDescriptor is ControllerActionDescriptor action && !BoilerplateApi.IsFrameworkType(action.ControllerTypeInfo.AsType()))
                {
                    var wrap = BoilerplateApi.GetWrapResult(action.MethodInfo, action.ControllerTypeInfo.AsType());
                    if (Wrap(context.Result, context.HttpContext, action.MethodInfo, wrap) is { } wrapped) context.Result = wrapped;
                }

                await next();
            }

            private static IActionResult? Wrap(IActionResult result, HttpContext http, MethodInfo method, WrapResultAttribute wrap)
            {
                switch (result)
                {
                    case ObjectResult { Value: AjaxResponseBase }:
                        return null;
                    case ObjectResult { Value: RemoteServiceErrorResponse error } failed:
                        if (!wrap.WrapOnError) return null;
                        var status = failed.StatusCode ?? http.Response.StatusCode;
                        return new ObjectResult(new AjaxResponse(BoilerplateApi.ToErrorInfo(error.Error), status is 401 or 403)) { StatusCode = status, DeclaredType = typeof(AjaxResponse) };
                    case ObjectResult value:
                        if (!wrap.WrapOnSuccess) return null;
                        return new ObjectResult(new AjaxResponse(value.Value)) { StatusCode = value.StatusCode, DeclaredType = typeof(AjaxResponse) };
                    case JsonResult json:
                        if (!wrap.WrapOnSuccess || json.Value is AjaxResponseBase) return null;
                        return new JsonResult(new AjaxResponse(json.Value), json.SerializerSettings) { StatusCode = json.StatusCode, ContentType = json.ContentType };
                    case EmptyResult or NoContentResult when method.ReturnType == typeof(void) || method.ReturnType == typeof(Task) || method.ReturnType == typeof(ValueTask):
                        if (!wrap.WrapOnSuccess) return null;
                        http.Response.StatusCode = StatusCodes.Status200OK;
                        return new ObjectResult(new AjaxResponse()) { StatusCode = StatusCodes.Status200OK, DeclaredType = typeof(AjaxResponse) };
                    default:
                        return null;
                }
            }
        }

        /// <summary>401/403 of the authorization middleware for the solution's actions → AjaxResponse with unAuthorizedRequest = true.</summary>
        public class BoilerplateAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
        {
            private readonly AuthorizationMiddlewareResultHandler _default = new();

            public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
            {
                if ((authorizeResult.Challenged || authorizeResult.Forbidden) &&
                    context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action &&
                    !BoilerplateApi.IsFrameworkType(action.ControllerTypeInfo.AsType()) &&
                    BoilerplateApi.GetWrapResult(action.MethodInfo, action.ControllerTypeInfo.AsType()).WrapOnError)
                {
                    var challenged = authorizeResult.Challenged && context.User.Identity?.IsAuthenticated != true;
                    context.Response.StatusCode = challenged ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden;
                    var message = challenged ? "Current user did not login to the application!" : "Required permissions are not granted.";
                    await context.Response.WriteAsJsonAsync(new AjaxResponse(new ErrorInfo(message), unAuthorizedRequest: true));
                    return;
                }

                await _default.HandleAsync(next, context, policy, authorizeResult);
            }
        }

        """;
}
