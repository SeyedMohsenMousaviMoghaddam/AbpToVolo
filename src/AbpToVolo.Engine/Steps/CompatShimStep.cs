using System.Text.RegularExpressions;
using AbpToVolo.Core;

namespace AbpToVolo.Steps;

/// <summary>
/// Small compatibility types for Boilerplate APIs that have no 1:1 ABP.io equivalent but are used all over a code base.
/// Each one is generated only when the converted code uses it (and the solution does not declare it), in the domain
/// project, in the solution's root namespace (visible to every layer). They keep the behaviour of the original API and
/// are listed in the report so they can be replaced by the ABP.io way over time.
/// </summary>
public sealed partial class CompatShimStep : IMigrationStep
{
    public const string ShimFolder = "AbpToVoloCompat";

    private sealed record Shim(string Name, Regex Usage, Regex Declaration, string FileName, Func<string, string> Code, string Advice,
        ProjectLayer Layer = ProjectLayer.Domain);

    private static readonly Shim[] Shims =
    [
        new("ObjectMapper.Map<TDestination>(source)", SingleTypeMap(), new Regex(@"class ObjectMapperCompatExtensions\b"), "ObjectMapperCompatExtensions.cs", ObjectMapperShim,
            "replace with ObjectMapper.Map<TSource, TDestination>(source)"),
        new("Abp.Quartz JobBase", new Regex(@":\s*JobBase\b"), new Regex(@"class JobBase\b"), "JobBase.cs", JobBaseShim,
            "derive the jobs from QuartzBackgroundWorkerBase (Volo.Abp.BackgroundWorkers.Quartz) or IJob"),
        new("IExtendableObject (ExtensionData + GetData/SetData)", new Regex(@"\bIExtendableObject\b"), new Regex(@"interface IExtendableObject\b"), "ExtendableObject.cs", ExtendableObjectShim,
            "move the values to IHasExtraProperties (ExtraProperties + GetProperty/SetProperty)"),
        new("ILocalizationManager.GetString / GetSource / GetAllSources", new Regex(@"(?i)\b(?:_?localization\w*|l)\.(?:GetString|GetSource|GetAllSources)\("), new Regex(@"class LocalizationManagerCompatExtensions\b"),
            "LocalizationManagerCompatExtensions.cs", LocalizationManagerShim, "inject IStringLocalizer<TResource> and use L[key]"),
        new("ICacheManager / ITypedCache", new Regex(@"\bICacheManager\b"), new Regex(@"interface ICacheManager\b"), "CacheManagerCompat.cs", CacheManagerShim,
            "use IDistributedCache<TCacheItem> (one typed cache item class per cache)"),
        new("Abp IActiveTransactionProvider (custom repositories)", new Regex(@"IActiveTransactionProvider"), new Regex(@"interface IActiveTransactionProvider"),
            "ActiveTransactionProvider.cs", ActiveTransactionProviderShim, "use (await GetDbContextAsync()).Database.CurrentTransaction in the repositories",
            ProjectLayer.EntityFrameworkCore),
        new("ISettingManager.GetSettingValueForApplication / ForTenant / ForUser, ChangeSettingFor*", SettingManagerUsage(),
            new Regex(@"class SettingManagerCompatExtensions\b"), "SettingManagerCompatExtensions.cs", SettingManagerShim,
            "use ISettingProvider.GetAsync / ISettingManager.GetOrNullGlobalAsync / SetGlobalAsync"),
        new("SimpleStringCipher", new Regex(@"\bSimpleStringCipher\b"), new Regex(@"class SimpleStringCipher\b"), SimpleStringCipherFile, SimpleStringCipherShim,
            "use IStringEncryptionService (keep the same pass phrase while old encrypted values exist)", ProjectLayer.DomainShared),
        new("ITenantCache", new Regex(@"\bITenantCache\b"), new Regex(@"interface ITenantCache\b"), "TenantCacheCompat.cs", TenantCacheShim,
            "use ITenantStore / ITenantRepository"),
        new("IClientInfoProvider", new Regex(@"\bIClientInfoProvider\b"), new Regex(@"interface IClientInfoProvider\b"), "ClientInfoProviderCompat.cs", ClientInfoProviderShim,
            "use IWebClientInfoProvider (Volo.Abp.AspNetCore.WebClientInfo)", ProjectLayer.HttpApi),
        new("IAbpSession.ToUserIdentifier()", new Regex(@"\.ToUserIdentifier\("), new Regex(@"class CurrentUserCompatExtensions\b"), "CurrentUserCompatExtensions.cs",
            CurrentUserShim, "use CurrentUser.GetId() (ABP.io identifies a user by its id)"),
        new("Active transaction / connection of custom repositories (opened as Boilerplate did)", new Regex(@"GetActive(Transaction|Connection)Compat|\bIActiveTransactionProvider\b"),
            new Regex(@"class DbContextTransactionCompatExtensions\b"), "DbContextTransactionCompatExtensions.cs", DbContextTransactionCompatShim,
            "ABP.io starts no transaction for GET requests: open the connection before raw ADO.NET commands", ProjectLayer.EntityFrameworkCore),
        new("Boilerplate extension methods (DateTime.ToUnixTimestamp, IEnumerable.IsNullOrEmpty)", new Regex(@"\.ToUnixTimestamp\(|\.IsNullOrEmpty\(\)"),
            new Regex(@"class BoilerplateExtensions\b"), "BoilerplateExtensions.cs", BoilerplateExtensionsShim,
            "ABP.io's IsNullOrEmpty is for strings and ICollection only; use DateTimeOffset.ToUnixTimeSeconds()", ProjectLayer.DomainShared),
        new("IAuthorizationHelper (authorization of a method in a filter)", new Regex(@"\bIAuthorizationHelper\b"), new Regex(@"interface IAuthorizationHelper\b"),
            "AuthorizationHelperCompat.cs", AuthorizationHelperShim, "use IMethodInvocationAuthorizationService.CheckAsync", ProjectLayer.HttpApi),
        new("IErrorInfoBuilder (AjaxResponse error of an exception)", new Regex(@"\bIErrorInfoBuilder\b"), new Regex(@"interface IErrorInfoBuilder\b"),
            "ErrorInfoBuilderCompat.cs", ErrorInfoBuilderShim, "use IExceptionToErrorInfoConverter", ProjectLayer.HttpApi),
        new("AbpHandledExceptionData (handled exception event)", new Regex(@"\bAbpHandledExceptionData\b"), new Regex(@"class AbpHandledExceptionData\b"),
            "AbpHandledExceptionData.cs", HandledExceptionDataShim, "use IExceptionSubscriber (ABP.io exception notifications)"),
        new("IOnlineClientManager (online SignalR clients)", new Regex(@"\bIOnlineClientManager\b"), new Regex(@"interface IOnlineClientManager\b"),
            "OnlineClientManagerCompat.cs", OnlineClientManagerShim, "the hubs must Add/Remove their connections (Boilerplate's AbpCommonHub did)"),
        new("IPermissionChecker.IsGranted (sync)", new Regex(@"\bPermissionChecker\.IsGranted\("), new Regex(@"class PermissionCheckerCompatExtensions\b"),
            "PermissionCheckerCompatExtensions.cs", PermissionCheckerShim, "use await PermissionChecker.IsGrantedAsync(name)"),
        new("LogInManager / AbpLoginResult / AbpLoginResultTypeHelper (Boilerplate login)", new Regex(@"\b(?:LogInManager|AbpLoginResult\w*)\b"),
            new Regex(@"class LogInManager\b"), "LogInManagerCompat.cs", LogInManagerShim,
            "log in with the Account module (OpenIddict /connect/token) or IdentityUserManager + SignInManager<IdentityUser>"),
    ];

    public string Name => "Compatibility shims";

    public void Execute(MigrationContext context)
    {
        var files = context.Projects
            .Where(p => p.Layer != ProjectLayer.Tests)
            .SelectMany(p => FileSystem.EnumerateFiles(p.Directory, "*.cs"))
            .Where(f => !context.IsExcluded(f) && !f.Contains(Path.DirectorySeparatorChar + ShimFolder + Path.DirectorySeparatorChar))
            .Select(File.ReadAllText)
            .ToList();

        AddSettingStoreShim(context, files);

        foreach (var shim in Shims)
        {
            if (Target(context, shim.Layer) is not var (project, ns)) continue;
            var uses = files.Sum(f => shim.Usage.Matches(f).Count);
            if (uses == 0 || files.Any(f => shim.Declaration.IsMatch(f))) continue;

            var path = Path.Combine(project.Directory, ShimFolder, shim.FileName);
            FileSystem.WriteText(path, shim.Code(ns));
            context.Report.Change(Name, $"Generated a compatibility type for {shim.Name} ({uses} usages keep compiling)", context.Relative(path));
            context.Report.Todo(Name, $"{uses} usages of {shim.Name} rely on the generated compatibility type; {shim.Advice}.", context.Relative(path));
        }
    }

    /// <summary>The project (domain, else application) and namespace (solution root) the shims are generated in.</summary>
    private static (ProjectInfo Project, string Namespace)? Target(MigrationContext context, ProjectLayer layer = ProjectLayer.Domain)
    {
        var project = context.Projects.FirstOrDefault(p => p.Layer == layer)
                      ?? context.Projects.FirstOrDefault(p => p.Layer == ProjectLayer.Domain)
                      ?? context.Projects.FirstOrDefault(p => p.Layer == ProjectLayer.Application);
        if (project is null) return null;
        var ns = context.SolutionName is { } solution && project.RootNamespace.StartsWith(solution, StringComparison.Ordinal) ? solution : project.RootNamespace;
        return (project, ns);
    }

    /// <summary>
    /// Generates ProtectedSetterCompatExtensions (used by compiler-guided fixes) once; returns its namespace.
    /// new Entity { Id = x } → new Entity { }.SetProtected(e => e.Id, x) (ABP.io entities have protected Id/audit setters).
    /// </summary>
    public static string? EnsureProtectedSetterShim(MigrationContext context)
    {
        if (Target(context) is not var (project, ns)) return null;
        var path = Path.Combine(project.Directory, ShimFolder, "ProtectedSetterCompatExtensions.cs");
        if (!File.Exists(path))
        {
            FileSystem.WriteText(path, ProtectedSetterShim(ns));
            context.Report.Change("Compatibility shims", "Generated ProtectedSetterCompatExtensions (SetProtected for Id / audit properties in object initializers)",
                context.Relative(path));
        }

        return ns;
    }

    /// <summary>
    /// Generates BoilerplatePermissionManager (used by compiler-guided fixes) once; returns its namespace.
    /// Boilerplate's IPermissionManager (PermissionManager.GetAllPermissions() in services) → the ABP.io permission definitions.
    /// </summary>
    public static string? EnsurePermissionManagerShim(MigrationContext context)
    {
        if (Target(context) is not var (project, ns)) return null;
        var path = Path.Combine(project.Directory, ShimFolder, "BoilerplatePermissionManager.cs");
        if (!File.Exists(path))
        {
            FileSystem.WriteText(path, PermissionManagerShim(ns, context.Options.IdType == IdTypeMode.Source));
            context.Report.Change("Compatibility shims", "Generated BoilerplatePermissionManager (Boilerplate's PermissionManager.GetAllPermissions... over IPermissionDefinitionManager)",
                context.Relative(path));
        }

        return ns;
    }

    private static string AuthorizationHelperShim(string ns) => $$"""
        using System;
        using System.Reflection;
        using System.Threading.Tasks;
        using Volo.Abp.Authorization;
        using Volo.Abp.DependencyInjection;
        using Volo.Abp.Threading;

        namespace {{ns}};

        /// <summary>
        /// Generated by AbpToVolo: Boilerplate's IAuthorizationHelper — checks the authorization attributes of a method and its
        /// class ([Authorize], [AllowAnonymous], permission attributes) with ABP.io's authorization; throws AbpAuthorizationException.
        /// </summary>
        public interface IAuthorizationHelper
        {
            Task AuthorizeAsync(MethodInfo methodInfo, Type type);

            void Authorize(MethodInfo methodInfo, Type type);
        }

        public class AuthorizationHelper : IAuthorizationHelper, ITransientDependency
        {
            private readonly IMethodInvocationAuthorizationService _authorizationService;

            public AuthorizationHelper(IMethodInvocationAuthorizationService authorizationService)
            {
                _authorizationService = authorizationService;
            }

            public Task AuthorizeAsync(MethodInfo methodInfo, Type type) =>
                _authorizationService.CheckAsync(new MethodInvocationAuthorizationContext(methodInfo));

            public void Authorize(MethodInfo methodInfo, Type type) => AsyncHelper.RunSync(() => AuthorizeAsync(methodInfo, type));
        }

        """;

    private static string ErrorInfoBuilderShim(string ns) => $$"""
        using System;
        using System.Linq;
        using Volo.Abp.AspNetCore.ExceptionHandling;
        using Volo.Abp.DependencyInjection;

        namespace {{ns}};

        /// <summary>Generated by AbpToVolo: Boilerplate's IErrorInfoBuilder — the AjaxResponse error of an exception, as ABP.io shows it.</summary>
        public interface IErrorInfoBuilder
        {
            ErrorInfo BuildForException(Exception exception);
        }

        public class ErrorInfoBuilder : IErrorInfoBuilder, ITransientDependency
        {
            private readonly IExceptionToErrorInfoConverter _converter;

            public ErrorInfoBuilder(IExceptionToErrorInfoConverter converter)
            {
                _converter = converter;
            }

            public ErrorInfo BuildForException(Exception exception)
            {
                var error = _converter.Convert(exception);
                return new ErrorInfo(error.Message, error.Details, int.TryParse(error.Code, out var code) ? code : 0)
                {
                    ValidationErrors = error.ValidationErrors?.Select(v => new ValidationErrorInfo(v.Message, v.Members)).ToArray()
                };
            }
        }

        """;

    private static string HandledExceptionDataShim(string ns) => $$"""
        using System;

        namespace {{ns}};

        /// <summary>Generated by AbpToVolo: Boilerplate's handled exception event (published on the local event bus).</summary>
        public class AbpHandledExceptionData
        {
            public Exception Exception { get; }

            public AbpHandledExceptionData(Exception exception)
            {
                Exception = exception;
            }
        }

        """;

    private static string OnlineClientManagerShim(string ns) => $$"""
        using System;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading.Tasks;
        using Volo.Abp.DependencyInjection;

        namespace {{ns}};

        /// <summary>Generated by AbpToVolo: Boilerplate's online client (a SignalR connection), with its source ids.</summary>
        public interface IOnlineClient
        {
            string ConnectionId { get; }

            string? IpAddress { get; }

            int? TenantId { get; }

            long? UserId { get; }

            DateTime ConnectTime { get; }

            Dictionary<string, object> Properties { get; }
        }

        public class OnlineClient : IOnlineClient
        {
            public OnlineClient(string connectionId, string? ipAddress, int? tenantId, long? userId)
            {
                ConnectionId = connectionId;
                IpAddress = ipAddress;
                TenantId = tenantId;
                UserId = userId;
            }

            public string ConnectionId { get; }

            public string? IpAddress { get; }

            public int? TenantId { get; }

            public long? UserId { get; }

            public DateTime ConnectTime { get; } = DateTime.Now;

            public Dictionary<string, object> Properties { get; } = new();
        }

        /// <summary>
        /// Generated by AbpToVolo: Boilerplate's IOnlineClientManager — in memory, like Boilerplate's default one. The hubs add
        /// and remove their connections (Boilerplate's AbpCommonHub did it); a user is online while one of them is connected.
        /// </summary>
        public interface IOnlineClientManager
        {
            void Add(IOnlineClient client);

            bool Remove(string connectionId);

            IOnlineClient? GetByConnectionIdOrNull(string connectionId);

            IReadOnlyList<IOnlineClient> GetAllClients();

            IReadOnlyList<IOnlineClient> GetAllByUserId(long userId);

            Task AddAsync(IOnlineClient client);

            Task<bool> RemoveAsync(string connectionId);

            Task<IOnlineClient?> GetByConnectionIdOrNullAsync(string connectionId);

            Task<IReadOnlyList<IOnlineClient>> GetAllClientsAsync();

            Task<IReadOnlyList<IOnlineClient>> GetAllByUserIdAsync(long userId);
        }

        [ExposeServices(typeof(IOnlineClientManager), typeof(OnlineClientManager))]
        public class OnlineClientManager : IOnlineClientManager, ISingletonDependency
        {
            private readonly ConcurrentDictionary<string, IOnlineClient> _clients = new();

            public void Add(IOnlineClient client) => _clients[client.ConnectionId] = client;

            public bool Remove(string connectionId) => _clients.TryRemove(connectionId, out _);

            public IOnlineClient? GetByConnectionIdOrNull(string connectionId) => _clients.TryGetValue(connectionId, out var client) ? client : null;

            public IReadOnlyList<IOnlineClient> GetAllClients() => _clients.Values.ToList();

            public IReadOnlyList<IOnlineClient> GetAllByUserId(long userId) => _clients.Values.Where(c => c.UserId == userId).ToList();

            public Task AddAsync(IOnlineClient client)
            {
                Add(client);
                return Task.CompletedTask;
            }

            public Task<bool> RemoveAsync(string connectionId) => Task.FromResult(Remove(connectionId));

            public Task<IOnlineClient?> GetByConnectionIdOrNullAsync(string connectionId) => Task.FromResult(GetByConnectionIdOrNull(connectionId));

            public Task<IReadOnlyList<IOnlineClient>> GetAllClientsAsync() => Task.FromResult(GetAllClients());

            public Task<IReadOnlyList<IOnlineClient>> GetAllByUserIdAsync(long userId) => Task.FromResult(GetAllByUserId(userId));
        }

        """;

    private static string DbContextTransactionCompatShim(string ns) => $$"""
        using System.Data;
        using System.Data.Common;
        using System.Threading.Tasks;
        using Microsoft.EntityFrameworkCore;
        using Microsoft.EntityFrameworkCore.Storage;

        namespace {{ns}};

        /// <summary>
        /// Generated by AbpToVolo: the transaction / connection raw ADO.NET commands of custom repositories use. Boilerplate's
        /// IActiveTransactionProvider started the unit of work's transaction, so the connection was open; ABP.io starts none for
        /// GET requests: the connection is opened here (EF Core closes it with the DbContext).
        /// </summary>
        public static class DbContextTransactionCompatExtensions
        {
            public static async Task<DbTransaction?> GetActiveTransactionCompatAsync(this DbContext dbContext)
            {
                await OpenAsync(dbContext);
                return dbContext.Database.CurrentTransaction?.GetDbTransaction();
            }

            public static async Task<DbConnection> GetActiveConnectionCompatAsync(this DbContext dbContext)
            {
                await OpenAsync(dbContext);
                return dbContext.Database.GetDbConnection();
            }

            public static DbTransaction? GetActiveTransactionCompat(this DbContext dbContext) => GetActiveTransactionCompatAsync(dbContext).GetAwaiter().GetResult();

            public static DbConnection GetActiveConnectionCompat(this DbContext dbContext) => GetActiveConnectionCompatAsync(dbContext).GetAwaiter().GetResult();

            private static async Task OpenAsync(DbContext dbContext)
            {
                if (dbContext.Database.GetDbConnection().State != ConnectionState.Open) await dbContext.Database.OpenConnectionAsync();
            }
        }

        """;

    private static string BoilerplateExtensionsShim(string ns) => $$"""
        using System;
        using System.Collections.Generic;
        using System.Linq;

        namespace {{ns}};

        /// <summary>Generated by AbpToVolo: extension methods of Boilerplate (Abp.Extensions, Abp.Timing) the code uses.</summary>
        public static class BoilerplateExtensions
        {
            /// <summary>Seconds since 1970-01-01 (the DateTime's kind), as Boilerplate's DateTimeExtensions.ToUnixTimestamp.</summary>
            public static long ToUnixTimestamp(this DateTime target) =>
                Convert.ToInt64((target - new DateTime(1970, 1, 1, 0, 0, 0, target.Kind)).TotalSeconds);

            /// <summary>Also for queries and sequences (ABP.io's is for ICollection); a query runs once (Any).</summary>
            public static bool IsNullOrEmpty<T>(this IEnumerable<T>? source) => source == null || !source.Any();
        }

        """;

    /// <summary>
    /// Module Zero's setting store API (ISettingStore.GetSettingOrNull / Create / Update with SettingInfo, the user settings
    /// cache) → SettingInfo + BoilerplateSettingStore over ABP.io's ISettingManager. The compiler-guided fixes switch the
    /// ISettingStore of such classes to BoilerplateSettingStore.
    /// </summary>
    private void AddSettingStoreShim(MigrationContext context, List<string> files)
    {
        var uses = files.Count(f => SettingStoreUsage().IsMatch(f));
        if (uses == 0 || files.Any(f => f.Contains("class BoilerplateSettingStore", StringComparison.Ordinal)) || Target(context) is not var (project, ns)) return;

        var legacyIds = context.Options.IdType == IdTypeMode.Source;
        var userCache = files.Any(f => f.Contains("GetUserSettingsCache", StringComparison.Ordinal)) && files.Any(f => Regex.IsMatch(f, @"\bICacheManager\b"));
        var path = Path.Combine(project.Directory, ShimFolder, "BoilerplateSettingStore.cs");
        FileSystem.WriteText(path, SettingStoreShim(ns, legacyIds, userCache));
        context.Report.Change(Name, $"Generated SettingInfo / BoilerplateSettingStore for Module Zero's setting store API ({uses} files)", context.Relative(path));
        context.Report.Todo(Name, "Module Zero's ISettingStore / SettingInfo calls use the generated BoilerplateSettingStore; prefer ISettingManager (SetForUserAsync, SetForTenantAsync, SetGlobalAsync).",
            context.Relative(path));
    }

    [GeneratedRegex(@"\bSettingInfo\b|\.GetSettingOrNull(Async)?\(|GetUserSettingsCache\(")]
    private static partial Regex SettingStoreUsage();

    private static string SettingStoreShim(string ns, bool legacyIds, bool userCache)
    {
        var userKey = legacyIds ? "long" : "Guid";
        var userGuid = legacyIds ? "LegacyUserIds.ToUserGuid(userId)" : "userId";
        var cache = userCache
            ? """

                /// <summary>Boilerplate's user settings cache: ABP.io invalidates its own setting cache, this one only keeps the code working.</summary>
                public static class UserSettingsCacheCompatExtensions
                {
                    public static ITypedCache<string, Dictionary<string, SettingInfo>> GetUserSettingsCache(this ICacheManager cacheManager) =>
                        cacheManager.GetCache<string, Dictionary<string, SettingInfo>>("AbpUserSettingsCache");
                }

                """
            : "";
        return $$"""
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Volo.Abp.DependencyInjection;
            using Volo.Abp.SettingManagement;
            using Volo.Abp.Threading;

            namespace {{ns}};

            /// <summary>Generated by AbpToVolo: Module Zero's setting row (tenant, user, name, value).</summary>
            public class SettingInfo
            {
                public Guid? TenantId { get; set; }

                public {{userKey}}? UserId { get; set; }

                public string Name { get; set; } = null!;

                public string? Value { get; set; }

                public SettingInfo()
                {
                }

                public SettingInfo(Guid? tenantId, {{userKey}}? userId, string name, string? value)
                {
                    TenantId = tenantId;
                    UserId = userId;
                    Name = name;
                    Value = value;
                }
            }

            /// <summary>
            /// Generated by AbpToVolo: Module Zero's ISettingStore over ABP.io's ISettingManager — a user's setting when UserId
            /// is set, else the tenant's when TenantId is set, else the application's (global) setting.
            /// </summary>
            public class BoilerplateSettingStore : ITransientDependency
            {
                protected ISettingManager SettingManager { get; }

                public BoilerplateSettingStore(ISettingManager settingManager)
                {
                    SettingManager = settingManager;
                }

                public virtual async Task<SettingInfo?> GetSettingOrNullAsync(Guid? tenantId, {{userKey}}? userId, string name)
                {
                    var value = userId is { } user ? await SettingManager.GetOrNullForUserAsync(name, UserGuid(user), fallback: false)
                        : tenantId is { } tenant ? await SettingManager.GetOrNullForTenantAsync(name, tenant, fallback: false)
                        : await SettingManager.GetOrNullGlobalAsync(name, fallback: false);
                    return value is null ? null : new SettingInfo(tenantId, userId, name, value);
                }

                public virtual SettingInfo? GetSettingOrNull(Guid? tenantId, {{userKey}}? userId, string name) =>
                    AsyncHelper.RunSync(() => GetSettingOrNullAsync(tenantId, userId, name));

                public virtual Task CreateAsync(SettingInfo setting) => SetAsync(setting, setting.Value);

                public virtual Task UpdateAsync(SettingInfo setting) => SetAsync(setting, setting.Value);

                public virtual Task DeleteAsync(SettingInfo setting) => SetAsync(setting, null);

                public virtual void Create(SettingInfo setting) => AsyncHelper.RunSync(() => CreateAsync(setting));

                public virtual void Update(SettingInfo setting) => AsyncHelper.RunSync(() => UpdateAsync(setting));

                public virtual void Delete(SettingInfo setting) => AsyncHelper.RunSync(() => DeleteAsync(setting));

                protected virtual Task SetAsync(SettingInfo setting, string? value) =>
                    setting.UserId is { } user ? SettingManager.SetForUserAsync(UserGuid(user), setting.Name, value, forceToSet: true)
                    : setting.TenantId is { } tenant ? SettingManager.SetForTenantAsync(tenant, setting.Name, value, forceToSet: true)
                    : SettingManager.SetGlobalAsync(setting.Name, value);

                protected virtual Guid UserGuid({{userKey}} userId) => {{userGuid}};
            }
            {{cache}}
            """;
    }

    /// <summary>Generates BoilerplateEntityDto (used by compiler-guided fixes) once; returns its namespace.</summary>
    public static string? EnsureEntityDtoShim(MigrationContext context)
    {
        if (Target(context) is not var (project, ns)) return null;
        var path = Path.Combine(project.Directory, ShimFolder, "BoilerplateEntityDto.cs");
        if (!File.Exists(path))
        {
            FileSystem.WriteText(path, $$"""
                using Volo.Abp.Application.Dtos;

                namespace {{ns}};

                /// <summary>Generated by AbpToVolo: Boilerplate's EntityDto&lt;TKey&gt; could be created (new EntityDto&lt;int&gt;(id)); ABP.io's is abstract.</summary>
                public class BoilerplateEntityDto<TKey> : EntityDto<TKey>
                {
                    public BoilerplateEntityDto()
                    {
                    }

                    public BoilerplateEntityDto(TKey id)
                    {
                        Id = id;
                    }
                }

                """);
            context.Report.Change("Compatibility shims", "Generated BoilerplateEntityDto (new EntityDto<T>(id) of Boilerplate code)", context.Relative(path));
        }

        return ns;
    }

    /// <summary>Generates ModuleEntityFactory (used by compiler-guided fixes) once; returns its namespace.</summary>
    public static string? EnsureModuleEntityFactoryShim(MigrationContext context)
    {
        if (Target(context) is not var (project, ns)) return null;
        var path = Path.Combine(project.Directory, ShimFolder, "ModuleEntityFactory.cs");
        if (!File.Exists(path))
        {
            FileSystem.WriteText(path, ModuleEntityFactoryShim(ns));
            context.Report.Change("Compatibility shims", "Generated ModuleEntityFactory (new Tenant() of Boilerplate code; ABP.io's constructor is internal)",
                context.Relative(path));
        }

        return ns;
    }

    private static string ModuleEntityFactoryShim(string ns) => $$"""
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading.Tasks;
        using Volo.Abp;
        using Volo.Abp.Data;
        using Volo.Abp.DependencyInjection;
        using Volo.Abp.Domain.Entities;
        using Volo.Abp.TenantManagement;

        namespace {{ns}};

        /// <summary>
        /// Generated by AbpToVolo: Boilerplate's tenant lookup by tenancy name (kept as the TenancyName extra property), else by name.
        /// </summary>
        public class BoilerplateTenantLookup : ITransientDependency
        {
            protected ITenantRepository TenantRepository { get; }

            public BoilerplateTenantLookup(ITenantRepository tenantRepository)
            {
                TenantRepository = tenantRepository;
            }

            public virtual async Task<Tenant?> FindByTenancyNameAsync(string tenancyName)
            {
                var tenants = await TenantRepository.GetListAsync();
                return tenants.FirstOrDefault(t => string.Equals(t.GetProperty<string>("TenancyName"), tenancyName, StringComparison.OrdinalIgnoreCase)) ??
                       tenants.FirstOrDefault(t => string.Equals(t.Name, tenancyName, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// Generated by AbpToVolo: module entities Boilerplate code creates with "new" while ABP.io's constructor is not public
        /// (new Tenant() + tenant.Name = …). Prefer ITenantManager.CreateAsync(name), which also checks the name.
        /// </summary>
        public static class ModuleEntityFactory
        {
            public static Tenant NewTenant(Guid id)
            {
                var tenant = (Tenant)Activator.CreateInstance(typeof(Tenant), nonPublic: true)!;
                EntityHelper.TrySetId(tenant, () => id, true);
                if (tenant.ConnectionStrings is null) ObjectHelper.TrySetProperty(tenant, t => t.ConnectionStrings, () => new List<TenantConnectionString>());
                return tenant;
            }
        }

        """;

    private static string PermissionManagerShim(string ns, bool legacyIds) => $$"""
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading.Tasks;
        using Volo.Abp.Authorization.Permissions;
        using Volo.Abp.DependencyInjection;
        using Volo.Abp.Identity;
        using Volo.Abp.MultiTenancy;
        using Volo.Abp.Threading;
        using IPermissionStore = Volo.Abp.PermissionManagement.IPermissionManager;

        namespace {{ns}};

        /// <summary>
        /// Generated by AbpToVolo: Boilerplate's IPermissionManager (the PermissionManager of its base classes) over the ABP.io
        /// permission definitions, and the permission methods of Module Zero's RoleManager / UserManager over ABP.io's permission
        /// grants (roles: provider "R" + role name, users: "U" + user id). tenancyFilter keeps the permissions of the current side
        /// (host / tenant), as in Boilerplate. ABP.io has no prohibitions: ProhibitPermissionAsync removes the user's own grant.
        /// </summary>
        public class BoilerplatePermissionManager : ITransientDependency
        {
            private const string RoleProvider = "R";
            private const string UserProvider = "U";

            protected IPermissionDefinitionManager PermissionDefinitionManager { get; }

            protected ICurrentTenant CurrentTenant { get; }

            protected IAbpLazyServiceProvider LazyServiceProvider { get; }

            protected IPermissionStore PermissionStore => LazyServiceProvider.LazyGetRequiredService<IPermissionStore>();

            public BoilerplatePermissionManager(IPermissionDefinitionManager permissionDefinitionManager, ICurrentTenant currentTenant,
                IAbpLazyServiceProvider lazyServiceProvider)
            {
                PermissionDefinitionManager = permissionDefinitionManager;
                CurrentTenant = currentTenant;
                LazyServiceProvider = lazyServiceProvider;
            }

            // ---------- Module Zero RoleManager ----------

            public virtual async Task<IReadOnlyList<PermissionDefinition>> GetGrantedPermissionsAsync(IdentityRole role) =>
                await GrantedAsync(RoleProvider, role.Name);

            public virtual async Task<IReadOnlyList<PermissionDefinition>> GetGrantedPermissionsAsync(Guid roleId) =>
                await GetGrantedPermissionsAsync(await LazyServiceProvider.LazyGetRequiredService<IIdentityRoleRepository>().GetAsync(roleId));

            public virtual async Task<IReadOnlyList<PermissionDefinition>> GetGrantedPermissionsAsync(string roleName) =>
                await GrantedAsync(RoleProvider, roleName);
        {{(legacyIds ? """

                    /// <summary>By the role's source id (--id-type source).</summary>
                    public virtual Task<IReadOnlyList<PermissionDefinition>> GetGrantedPermissionsAsync(int legacyRoleId) =>
                        GetGrantedPermissionsAsync(LegacyRoleIds.ToRoleGuid(legacyRoleId));

                    /// <summary>By the role's source id (--id-type source).</summary>
                    public virtual Task SetGrantedPermissionsAsync(int legacyRoleId, IEnumerable<PermissionDefinition> permissions) =>
                        SetGrantedPermissionsAsync(LegacyRoleIds.ToRoleGuid(legacyRoleId), permissions);

            """ : "")}}

            public virtual Task SetGrantedPermissionsAsync(IdentityRole role, IEnumerable<PermissionDefinition> permissions) =>
                SetGrantedAsync(RoleProvider, role.Name, permissions);

            public virtual async Task SetGrantedPermissionsAsync(Guid roleId, IEnumerable<PermissionDefinition> permissions) =>
                await SetGrantedPermissionsAsync(await LazyServiceProvider.LazyGetRequiredService<IIdentityRoleRepository>().GetAsync(roleId), permissions);

            public virtual Task GrantPermissionAsync(IdentityRole role, PermissionDefinition permission) =>
                PermissionStore.SetAsync(permission.Name, RoleProvider, role.Name, true);

            public virtual Task ProhibitPermissionAsync(IdentityRole role, PermissionDefinition permission) =>
                PermissionStore.SetAsync(permission.Name, RoleProvider, role.Name, false);

            public virtual Task ResetAllPermissionsAsync(IdentityRole role) => PermissionStore.DeleteAsync(RoleProvider, role.Name);

            // ---------- Module Zero UserManager ----------

            /// <summary>The user's permissions: granted to the user or to one of the user's roles.</summary>
            public virtual async Task<IReadOnlyList<PermissionDefinition>> GetGrantedPermissionsAsync(IdentityUser user)
            {
                var names = (await GrantedAsync(UserProvider, user.Id.ToString())).Select(p => p.Name).ToHashSet();
                foreach (var role in await LazyServiceProvider.LazyGetRequiredService<IdentityUserManager>().GetRolesAsync(user))
                {
                    names.UnionWith((await GrantedAsync(RoleProvider, role)).Select(p => p.Name));
                }

                return (await GetAllPermissionsAsync()).Where(p => names.Contains(p.Name)).ToList();
            }

            /// <summary>The user's own grants become exactly these permissions.</summary>
            public virtual Task SetGrantedPermissionsAsync(IdentityUser user, IEnumerable<PermissionDefinition> permissions) =>
                SetGrantedAsync(UserProvider, user.Id.ToString(), permissions);

            public virtual Task GrantPermissionAsync(IdentityUser user, PermissionDefinition permission) =>
                PermissionStore.SetAsync(permission.Name, UserProvider, user.Id.ToString(), true);

            public virtual Task ProhibitPermissionAsync(IdentityUser user, PermissionDefinition permission) =>
                PermissionStore.SetAsync(permission.Name, UserProvider, user.Id.ToString(), false);

            public virtual Task ResetAllPermissionsAsync(IdentityUser user) => PermissionStore.DeleteAsync(UserProvider, user.Id.ToString());

            public virtual async Task<bool> IsGrantedAsync(IdentityUser user, string permissionName) =>
                (await GetGrantedPermissionsAsync(user)).Any(p => p.Name == permissionName);

            public virtual Task<bool> IsGrantedAsync(IdentityUser user, PermissionDefinition permission) => IsGrantedAsync(user, permission.Name);

            protected virtual async Task<IReadOnlyList<PermissionDefinition>> GrantedAsync(string providerName, string providerKey)
            {
                var granted = (await PermissionStore.GetAllAsync(providerName, providerKey)).Where(p => p.IsGranted).Select(p => p.Name).ToHashSet();
                return (await GetAllPermissionsAsync()).Where(p => granted.Contains(p.Name)).ToList();
            }

            /// <summary>Only the differences are written (grants added, grants removed).</summary>
            protected virtual async Task SetGrantedAsync(string providerName, string providerKey, IEnumerable<PermissionDefinition> permissions)
            {
                var wanted = permissions.Select(p => p.Name).ToHashSet();
                var current = (await GrantedAsync(providerName, providerKey)).Select(p => p.Name).ToHashSet();
                foreach (var name in wanted.Except(current)) await PermissionStore.SetAsync(name, providerName, providerKey, true);
                foreach (var name in current.Except(wanted)) await PermissionStore.SetAsync(name, providerName, providerKey, false);
            }

            public virtual async Task<IReadOnlyList<PermissionDefinition>> GetAllPermissionsAsync(bool tenancyFilter = true)
            {
                var permissions = await PermissionDefinitionManager.GetPermissionsAsync();
                if (!tenancyFilter) return permissions;
                var side = CurrentTenant.IsAvailable ? MultiTenancySides.Tenant : MultiTenancySides.Host;
                return permissions.Where(p => p.MultiTenancySide.HasFlag(side)).ToList();
            }

            public virtual IReadOnlyList<PermissionDefinition> GetAllPermissions(bool tenancyFilter = true) =>
                AsyncHelper.RunSync(() => GetAllPermissionsAsync(tenancyFilter));

            public virtual PermissionDefinition GetPermission(string name) => AsyncHelper.RunSync(() => PermissionDefinitionManager.GetAsync(name));

            public virtual PermissionDefinition? GetPermissionOrNull(string name) => AsyncHelper.RunSync(() => PermissionDefinitionManager.GetOrNullAsync(name));
        }

        """;

    private static string ProtectedSetterShim(string ns) => $$"""
        using System;
        using System.Linq.Expressions;
        using Volo.Abp;

        namespace {{ns}};

        /// <summary>
        /// Generated by AbpToVolo: sets properties with protected setters (Entity.Id, CreatorId...) inside expressions,
        /// for Boilerplate code like "new Person { Id = id, ... }". ABP.io keeps an explicitly set CreatorId.
        /// Prefer a constructor taking the id (ABP.io entity convention).
        /// </summary>
        public static class ProtectedSetterCompatExtensions
        {
            public static TObject SetProtected<TObject, TValue>(this TObject obj, Expression<Func<TObject, TValue>> property, TValue value)
            {
                ObjectHelper.TrySetProperty(obj, property, () => value);
                return obj;
            }
        }

        """;

    private static string ActiveTransactionProviderShim(string ns) => $$"""
        using System;
        using System.Collections.Generic;
        using System.Data;
        using System.Threading.Tasks;
        using Microsoft.EntityFrameworkCore;
        using Microsoft.EntityFrameworkCore.Storage;
        using Microsoft.Extensions.DependencyInjection;
        using Volo.Abp.DependencyInjection;
        using Volo.Abp.EntityFrameworkCore;

        namespace {{ns}};

        /// <summary>Generated by AbpToVolo: Boilerplate's ActiveTransactionProviderArgs ("ContextType" = the DbContext type).</summary>
        public class ActiveTransactionProviderArgs : Dictionary<string, object>
        {
        }

        /// <summary>
        /// Generated by AbpToVolo: Boilerplate's IActiveTransactionProvider (used by custom repositories for raw ADO.NET
        /// commands) on top of ABP.io's IDbContextProvider. In ABP.io repositories prefer
        /// (await GetDbContextAsync()).Database.CurrentTransaction?.GetDbTransaction().
        /// </summary>
        public interface IActiveTransactionProvider
        {
            Task<IDbTransaction> GetActiveTransactionAsync(ActiveTransactionProviderArgs args);

            IDbTransaction GetActiveTransaction(ActiveTransactionProviderArgs args);

            Task<IDbConnection> GetActiveConnectionAsync(ActiveTransactionProviderArgs args);

            IDbConnection GetActiveConnection(ActiveTransactionProviderArgs args);
        }

        public class EfCoreActiveTransactionProvider : IActiveTransactionProvider, ITransientDependency
        {
            private readonly IServiceProvider _serviceProvider;

            public EfCoreActiveTransactionProvider(IServiceProvider serviceProvider)
            {
                _serviceProvider = serviceProvider;
            }

            // Boilerplate's provider started the transaction (the connection was open); ABP.io starts none for GET requests.
            public async Task<IDbTransaction> GetActiveTransactionAsync(ActiveTransactionProviderArgs args) =>
                await (await GetDbContextAsync(args)).GetActiveTransactionCompatAsync();

            public IDbTransaction GetActiveTransaction(ActiveTransactionProviderArgs args) => GetActiveTransactionAsync(args).GetAwaiter().GetResult();

            public async Task<IDbConnection> GetActiveConnectionAsync(ActiveTransactionProviderArgs args) =>
                await (await GetDbContextAsync(args)).GetActiveConnectionCompatAsync();

            public IDbConnection GetActiveConnection(ActiveTransactionProviderArgs args) => GetActiveConnectionAsync(args).GetAwaiter().GetResult();

            private async Task<DbContext> GetDbContextAsync(ActiveTransactionProviderArgs args)
            {
                var contextType = (Type)args["ContextType"];
                var providerType = typeof(IDbContextProvider<>).MakeGenericType(contextType);
                var provider = _serviceProvider.GetRequiredService(providerType);
                var task = (Task)providerType.GetMethod("GetDbContextAsync", Type.EmptyTypes)!.Invoke(provider, null)!;
                await task;
                return (DbContext)task.GetType().GetProperty("Result")!.GetValue(task)!;
            }
        }

        """;

    private static string ObjectMapperShim(string ns) => $$"""
        using System.Linq;
        using System.Reflection;
        using Volo.Abp.ObjectMapping;

        namespace {{ns}};

        /// <summary>
        /// Generated by AbpToVolo: keeps Boilerplate-style ObjectMapper.Map&lt;TDestination&gt;(source) calls working.
        /// Prefer ObjectMapper.Map&lt;TSource, TDestination&gt;(source) and delete this class when no call is left.
        /// </summary>
        public static class ObjectMapperCompatExtensions
        {
            private static readonly MethodInfo MapMethod = typeof(IObjectMapper).GetMethods()
                .Single(m => m.Name == nameof(IObjectMapper.Map) && m.GetGenericArguments().Length == 2 && m.GetParameters().Length == 1);

            public static TDestination Map<TDestination>(this IObjectMapper mapper, object source)
            {
                if (source is null)
                {
                    return default!;
                }

                return (TDestination)MapMethod.MakeGenericMethod(source.GetType(), typeof(TDestination)).Invoke(mapper, [source])!;
            }
        }

        """;

    private static string JobBaseShim(string ns) => $$"""
        using System.Threading.Tasks;
        using Microsoft.Extensions.Logging;
        using Microsoft.Extensions.Logging.Abstractions;
        using Quartz;
        using Volo.Abp.DependencyInjection;

        namespace {{ns}};

        /// <summary>
        /// Generated by AbpToVolo: replacement of Abp.Quartz's JobBase, so existing Quartz jobs keep compiling.
        /// With Volo.Abp.BackgroundWorkers.Quartz the jobs are created by dependency injection. Prefer
        /// QuartzBackgroundWorkerBase (ABP.io) for new jobs.
        /// </summary>
        public abstract class JobBase : IJob, ITransientDependency
        {
            public ILogger Logger { get; set; } = NullLogger.Instance;

            public abstract Task Execute(IJobExecutionContext context);
        }

        """;

    private static string ExtendableObjectShim(string ns) => $$"""
        using System.Text.Json;
        using System.Text.Json.Nodes;

        namespace {{ns}};

        /// <summary>
        /// Generated by AbpToVolo: Boilerplate's IExtendableObject (JSON stored in the ExtensionData column) with its
        /// GetData/SetData/RemoveData extensions. ABP.io uses IHasExtraProperties (ExtraProperties + GetProperty/SetProperty).
        /// </summary>
        public interface IExtendableObject
        {
            string ExtensionData { get; set; }
        }

        public static class ExtendableObjectExtensions
        {
            public static T GetData<T>(this IExtendableObject extendableObject, string name, bool handleType = false)
            {
                var json = Parse(extendableObject);
                return json.TryGetPropertyValue(name, out var value) && value is not null ? value.Deserialize<T>()! : default!;
            }

            public static void SetData<T>(this IExtendableObject extendableObject, string name, T value, bool handleType = false)
            {
                var json = Parse(extendableObject);
                if (value is null)
                {
                    json.Remove(name);
                }
                else
                {
                    json[name] = JsonSerializer.SerializeToNode(value);
                }

                extendableObject.ExtensionData = json.Count == 0 ? null : json.ToJsonString();
            }

            public static bool RemoveData(this IExtendableObject extendableObject, string name)
            {
                var json = Parse(extendableObject);
                var removed = json.Remove(name);
                extendableObject.ExtensionData = json.Count == 0 ? null : json.ToJsonString();
                return removed;
            }

            private static JsonObject Parse(IExtendableObject extendableObject) =>
                string.IsNullOrWhiteSpace(extendableObject.ExtensionData)
                    ? new JsonObject()
                    : JsonNode.Parse(extendableObject.ExtensionData) as JsonObject ?? new JsonObject();
        }

        """;

    private static string LocalizationManagerShim(string ns) => $$"""
        using System.Collections.Generic;
        using System.Globalization;
        using System.Linq;
        using Microsoft.Extensions.Localization;
        using Microsoft.Extensions.Options;
        using Volo.Abp.DependencyInjection;
        using Volo.Abp.Localization;

        namespace {{ns}};

        /// <summary>
        /// Generated by AbpToVolo: Boilerplate's ILocalizationManager (GetString, GetSource, GetAllSources) on top of
        /// ABP.io's IStringLocalizerFactory. Boilerplate source names are ABP.io resource names ([LocalizationResourceName]).
        /// </summary>
        public static class LocalizationManagerCompatExtensions
        {
            /// <summary>Every localization resource (Boilerplate: ILocalizationManager.GetAllSources()).</summary>
            public static IReadOnlyList<LocalizationSourceCompat> GetAllSources(this IStringLocalizerFactory factory) =>
                LocalizationSourcesCompat.ResourceNames
                    .Select(name => factory.CreateByResourceNameOrNull(name) is { } localizer ? new LocalizationSourceCompat(name, localizer) : null)
                    .OfType<LocalizationSourceCompat>()
                    .ToList();

            /// <summary>Boilerplate: ILocalizationManager.GetSource(name).</summary>
            public static LocalizationSourceCompat GetSource(this IStringLocalizerFactory factory, string sourceName) =>
                new(sourceName, factory.CreateByResourceNameOrNull(sourceName) ?? factory.CreateDefaultOrNull() ??
                                throw new KeyNotFoundException($"No localization resource named '{sourceName}'."));

            public static string GetString(this IStringLocalizerFactory factory, string sourceName, string name)
            {
                var localizer = factory.CreateByResourceNameOrNull(sourceName) ?? factory.CreateDefaultOrNull();
                return localizer is null ? name : localizer[name].Value;
            }

            public static string GetString(this IStringLocalizerFactory factory, string sourceName, string name, CultureInfo culture)
            {
                using (CultureHelper.Use(culture))
                {
                    return factory.GetString(sourceName, name);
                }
            }
        }

        /// <summary>A localization resource seen as a Boilerplate ILocalizationSource (Name, GetString, GetAllStrings).</summary>
        public sealed class LocalizationSourceCompat
        {
            private readonly IStringLocalizer _localizer;

            public LocalizationSourceCompat(string name, IStringLocalizer localizer)
            {
                Name = name;
                _localizer = localizer;
            }

            public string Name { get; }

            public string GetString(string name) => _localizer[name].Value;

            public string GetString(string name, CultureInfo culture)
            {
                using (CultureHelper.Use(culture))
                {
                    return _localizer[name].Value;
                }
            }

            public IReadOnlyList<LocalizedString> GetAllStrings(bool includeDefaults = true) => _localizer.GetAllStrings(includeDefaults).ToList();

            public IReadOnlyList<LocalizedString> GetAllStrings(CultureInfo culture, bool includeDefaults = true)
            {
                using (CultureHelper.Use(culture))
                {
                    return _localizer.GetAllStrings(includeDefaults).ToList();
                }
            }
        }

        /// <summary>Keeps the names of the configured localization resources (ABP.io does not expose them on the factory).</summary>
        [ExposeServices(typeof(IPostConfigureOptions<AbpLocalizationOptions>))]
        public class LocalizationSourcesCompat : IPostConfigureOptions<AbpLocalizationOptions>, ISingletonDependency
        {
            private static IReadOnlyList<string> _resourceNames = [];

            public static IReadOnlyList<string> ResourceNames => _resourceNames;

            public void PostConfigure(string? name, AbpLocalizationOptions options) =>
                _resourceNames = options.Resources.Keys.OrderBy(n => n, System.StringComparer.Ordinal).ToList();
        }

        """;

    private static string CacheManagerShim(string ns) => $$"""
        using System;
        using System.Text.Json;
        using System.Threading.Tasks;
        using Microsoft.Extensions.Caching.Distributed;
        using Volo.Abp.DependencyInjection;

        namespace {{ns}};

        /// <summary>
        /// Generated by AbpToVolo: Boilerplate's ICacheManager / ITypedCache (named caches) on top of the distributed cache
        /// ABP.io configures (memory or Redis). Values are stored as JSON under "{cacheName}:{generation}:{key}";
        /// Clear() starts a new generation. Prefer IDistributedCache&lt;TCacheItem&gt; for new code.
        /// </summary>
        public interface ICacheManager
        {
            ITypedCache<TKey, TValue> GetCache<TKey, TValue>(string name);

            ITypedCache<object, object> GetCache(string name);
        }

        public interface ITypedCache<TKey, TValue>
        {
            string Name { get; }

            TimeSpan DefaultSlidingExpireTime { get; set; }

            TValue Get(TKey key, Func<TKey, TValue> factory);

            TValue Get(TKey key, Func<TValue> factory);

            Task<TValue> GetAsync(TKey key, Func<TKey, Task<TValue>> factory);

            Task<TValue> GetAsync(TKey key, Func<Task<TValue>> factory);

            TValue GetOrDefault(TKey key);

            Task<TValue> GetOrDefaultAsync(TKey key);

            void Set(TKey key, TValue value, TimeSpan? slidingExpireTime = null, TimeSpan? absoluteExpireTime = null);

            Task SetAsync(TKey key, TValue value, TimeSpan? slidingExpireTime = null, TimeSpan? absoluteExpireTime = null);

            void Remove(TKey key);

            Task RemoveAsync(TKey key);

            void Clear();

            Task ClearAsync();
        }

        /* ABP.io exposes a service only under interfaces its class name ends with: ICacheManager is exposed explicitly. */
        [ExposeServices(typeof(ICacheManager), typeof(CacheManagerCompat))]
        public class CacheManagerCompat : ICacheManager, ISingletonDependency
        {
            private readonly IDistributedCache _cache;

            public CacheManagerCompat(IDistributedCache cache)
            {
                _cache = cache;
            }

            public ITypedCache<TKey, TValue> GetCache<TKey, TValue>(string name) => new TypedCacheCompat<TKey, TValue>(_cache, name);

            public ITypedCache<object, object> GetCache(string name) => new TypedCacheCompat<object, object>(_cache, name);
        }

        public class TypedCacheCompat<TKey, TValue> : ITypedCache<TKey, TValue>
        {
            private readonly IDistributedCache _cache;

            public TypedCacheCompat(IDistributedCache cache, string name)
            {
                _cache = cache;
                Name = name;
            }

            public string Name { get; }

            public TimeSpan DefaultSlidingExpireTime { get; set; } = TimeSpan.FromHours(1);

            public TValue Get(TKey key, Func<TKey, TValue> factory) => GetAsync(key, k => Task.FromResult(factory(k))).GetAwaiter().GetResult();

            public TValue Get(TKey key, Func<TValue> factory) => Get(key, _ => factory());

            public Task<TValue> GetAsync(TKey key, Func<Task<TValue>> factory) => GetAsync(key, _ => factory());

            public async Task<TValue> GetAsync(TKey key, Func<TKey, Task<TValue>> factory)
            {
                var bytes = await _cache.GetAsync(await KeyAsync(key));
                if (bytes is not null)
                {
                    return JsonSerializer.Deserialize<TValue>(bytes)!;
                }

                var value = await factory(key);
                await SetAsync(key, value);
                return value;
            }

            public TValue GetOrDefault(TKey key) => GetOrDefaultAsync(key).GetAwaiter().GetResult();

            public async Task<TValue> GetOrDefaultAsync(TKey key)
            {
                var bytes = await _cache.GetAsync(await KeyAsync(key));
                return bytes is null ? default! : JsonSerializer.Deserialize<TValue>(bytes)!;
            }

            public void Set(TKey key, TValue value, TimeSpan? slidingExpireTime = null, TimeSpan? absoluteExpireTime = null) =>
                SetAsync(key, value, slidingExpireTime, absoluteExpireTime).GetAwaiter().GetResult();

            public async Task SetAsync(TKey key, TValue value, TimeSpan? slidingExpireTime = null, TimeSpan? absoluteExpireTime = null)
            {
                var options = new DistributedCacheEntryOptions();
                if (absoluteExpireTime.HasValue)
                {
                    options.AbsoluteExpirationRelativeToNow = absoluteExpireTime;
                }
                else
                {
                    options.SlidingExpiration = slidingExpireTime ?? DefaultSlidingExpireTime;
                }

                await _cache.SetAsync(await KeyAsync(key), JsonSerializer.SerializeToUtf8Bytes(value), options);
            }

            public void Remove(TKey key) => RemoveAsync(key).GetAwaiter().GetResult();

            public async Task RemoveAsync(TKey key) => await _cache.RemoveAsync(await KeyAsync(key));

            public void Clear() => ClearAsync().GetAwaiter().GetResult();

            public Task ClearAsync() => _cache.SetStringAsync(GenerationKey, Guid.NewGuid().ToString("N"));

            private string GenerationKey => $"AbpToVoloCompat:{Name}:generation";

            private async Task<string> KeyAsync(TKey key)
            {
                var generation = await _cache.GetStringAsync(GenerationKey);
                if (generation is null)
                {
                    generation = "0";
                    await _cache.SetStringAsync(GenerationKey, generation);
                }

                return $"{Name}:{generation}:{key}";
            }
        }

        """;

    [GeneratedRegex(@"\bObjectMapper\.Map<(?:[^<>,]|<[^<>]*>)+>\(")]
    private static partial Regex SingleTypeMap();
}
