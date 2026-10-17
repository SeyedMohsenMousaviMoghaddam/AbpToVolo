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

            public async Task<IDbTransaction> GetActiveTransactionAsync(ActiveTransactionProviderArgs args) =>
                (await GetDbContextAsync(args)).Database.CurrentTransaction?.GetDbTransaction();

            public IDbTransaction GetActiveTransaction(ActiveTransactionProviderArgs args) => GetActiveTransactionAsync(args).GetAwaiter().GetResult();

            public async Task<IDbConnection> GetActiveConnectionAsync(ActiveTransactionProviderArgs args) =>
                (await GetDbContextAsync(args)).Database.GetDbConnection();

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
