using AbpToVolo.CodeMigration;
using AbpToVolo.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.Steps;

/// <summary>
/// IdTypeMode.Source (default): the application's references to users and roles keep their source key type (long/int)
/// while the ABP.io Identity entities have Guid keys. Each IdentityUser/IdentityRole gets the old id as LegacyId
/// (module entity extension + column, filled by the data script, then numbered from a sequence for new rows) and the
/// application's relationships point to it:
///   Domain:  LegacyIds (GetLegacyId() for IdentityUser/IdentityRole/ICurrentUser), a claims contributor that adds the
///            current user's legacy id ("legacy_user_id", also a dynamic claim), ILegacyIdLookup (find by legacy id),
///            the LegacyId extension properties;
///   EF Core: the LegacyId columns (default from the sequences), builder.ConfigureLegacyIds() in OnModelCreating
///            (sequences + HasPrincipalKey("LegacyId") for every navigation to IdentityUser/IdentityRole with a long/int
///            foreign key), the ILegacyIdLookup implementation.
/// ABP.io module fields (TenantId, CreatorId...) are Guid in both modes.
/// </summary>
public sealed class LegacyIdStep : IMigrationStep
{
    public string Name => "Legacy ids (source key types)";

    public bool IsEnabled(MigrationOptions options) => options.ConvertModuleZero && options.IdType == IdTypeMode.Source;

    public void Execute(MigrationContext context)
    {
        if (!context.UsesIdentityModules) return;
        var domain = context.FindProject(ProjectLayer.Domain);
        var efCore = context.FindProject(ProjectLayer.EntityFrameworkCore);
        if (domain is null || efCore is null) return;

        var prefix = new string(context.ModulePrefix.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        var ns = context.SolutionName is { } solution && domain.RootNamespace.StartsWith(solution, StringComparison.Ordinal) ? solution : domain.RootNamespace;

        // Domain
        var domainFile = Path.Combine(domain.Directory, CompatShimStep.ShimFolder, "LegacyIds.cs");
        FileSystem.WriteText(domainFile, DomainCode(ns));
        WireStatement(context, domain, ProjectLayer.Domain, "PreConfigureServices", "LegacyIds.ConfigureExtensionProperties();", ns);
        WireStatement(context, domain, ProjectLayer.Domain, "ConfigureServices",
            "Configure<global::Volo.Abp.Security.Claims.AbpClaimsPrincipalFactoryOptions>(options => { if (!options.DynamicClaims.Contains(LegacyIds.UserClaimType)) options.DynamicClaims.Add(LegacyIds.UserClaimType); });", ns);

        // EF Core
        var efFile = Path.Combine(efCore.Directory, "EntityFrameworkCore", "LegacyIdsModelBuilderExtensions.cs");
        FileSystem.WriteText(efFile, EfCoreCode(ns, efCore.RootNamespace + ".EntityFrameworkCore"));
        WireStatement(context, efCore, ProjectLayer.EntityFrameworkCore, "PreConfigureServices", "LegacyIdsEfCoreMappings.Configure();", efCore.RootNamespace + ".EntityFrameworkCore");
        var dbContext = AddModelBuilderCall(context, efCore, efCore.RootNamespace + ".EntityFrameworkCore");

        // Tenant (int) and user (long) ids: source type in the application's DTOs/models, Guid in the entities (ABP.io).
        var shared = context.FindProject(ProjectLayer.DomainShared) ?? domain;
        var mapsFile = Path.Combine(shared.Directory, CompatShimStep.ShimFolder, "LegacyIdMaps.cs");
        FileSystem.WriteText(mapsFile, LegacyIdMapsCode(ns));
        WireStatement(context, efCore, ProjectLayer.EntityFrameworkCore, "OnApplicationInitialization",
            "LegacyIdMapsResolver.Initialize(context.ServiceProvider);", efCore.RootNamespace + ".EntityFrameworkCore",
            "ApplicationInitializationContext context");

        context.Report.Change(Name, "IdentityUser/IdentityRole.LegacyId (old ids, then sequences), GetLegacyId() helpers, legacy_user_id claim, " +
                                    "ILegacyIdLookup; the application's long/int references to users/roles use HasPrincipalKey(\"LegacyId\")",
            context.Relative(domainFile));
        if (dbContext is null)
        {
            context.Report.Todo(Name, "Call builder.ConfigureLegacyIds() at the end of OnModelCreating of the application DbContext.", context.Relative(efFile));
        }

        context.Report.Todo(Name, "User/role references keep their source type (--id-type source): compare them with CurrentUser.GetLegacyId() / user.GetLegacyId(), " +
                                  "and find users by legacy id with ILegacyIdLookup. Use --id-type guid to convert them to Guid instead.", context.Relative(domainFile));
    }

    private static void WireStatement(MigrationContext context, ProjectInfo project, ProjectLayer layer, string method, string statement, string ns,
        string parameter = "ServiceConfigurationContext context")
    {
        if (AbpModulesStep.FindModuleFile(project, AbpModulesStep.ModuleNameFilters[layer]) is not { } file) return;
        var text = File.ReadAllText(file);
        var updated = ModuleMethodEditor.AddStatement(text, AbpModulesStep.ModuleNameFilters[layer], method, parameter, statement);
        if (updated is null || updated == text) return;
        FileSystem.WriteText(file, ModuleMethodEditor.AddUsings(updated, [ns]));
    }

    /// <summary>builder.ConfigureLegacyIds() at the end of OnModelCreating of the application DbContext.</summary>
    private static string? AddModelBuilderCall(MigrationContext context, ProjectInfo efCore, string ns)
    {
        foreach (var file in FileSystem.EnumerateFiles(efCore.Directory, "*.cs").Where(f => !context.IsExcluded(f)))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("AbpDbContext<", StringComparison.Ordinal) || !text.Contains("OnModelCreating", StringComparison.Ordinal)) continue;

            var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
            var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == "OnModelCreating" && m.Body is not null && m.ParameterList.Parameters.Count == 1 &&
                                     m.Ancestors().OfType<ClassDeclarationSyntax>().Any(c => SyntaxHelpers.HasBaseType(c, "AbpDbContext")));
            if (method is null) continue;
            if (method.Body!.ToString().Contains("ConfigureLegacyIds", StringComparison.Ordinal)) return file;

            var builder = method.ParameterList.Parameters[0].Identifier.Text;
            var indent = method.Body.Statements.Count > 0 ? SyntaxHelpers.Indentation(method.Body.Statements[0]) : SyntaxHelpers.Indentation(method) + "    ";
            var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var call = ParseStatement($"{builder}.ConfigureLegacyIds(useSequences: Database.ProviderName?.EndsWith(\"SqlServer\", global::System.StringComparison.Ordinal) == true);")
                .WithLeadingTrivia(EndOfLine(nl), Whitespace(indent), Comment("/* AbpToVolo (--id-type source): long/int references to users and roles → IdentityUser/IdentityRole.LegacyId */"),
                    EndOfLine(nl), Whitespace(indent))
                .WithTrailingTrivia(EndOfLine(nl));
            var updated = root.ReplaceNode(method.Body, method.Body.WithStatements(method.Body.Statements.Add(call)));
            FileSystem.WriteText(file, ModuleMethodEditor.AddUsings(updated.ToFullString(), [ns]), FileSystem.ReadText(file).Encoding);
            return file;
        }

        return null;
    }

    internal static string LegacyIdMapsCode(string ns) => $$"""
        using System;
        using System.Collections.Concurrent;

        namespace {{ns}};

        /// <summary>
        /// Generated by AbpToVolo (--id-type source): the application's DTOs and models keep the int tenant ids of the source
        /// (the API does not change), the entities use the ABP.io tenant ids (Guid). The tenant's old id is Tenant.LegacyId;
        /// these methods convert between them where the two meet (cached; resolved from the database the first time).
        /// </summary>
        {{MapClass("LegacyTenantIds", "int", "Tenant")}}

        /// <summary>
        /// Generated by AbpToVolo (--id-type source): the source (long) user ids of the DTOs and models ↔ the ABP.io user ids
        /// (Guid) of the entities, CurrentUser.Id... The user's old id is IdentityUser.LegacyId.
        /// </summary>
        {{MapClass("LegacyUserIds", "long", "User")}}

        /// <summary>
        /// Generated by AbpToVolo (--id-type source): the source (int) role ids of the application's tables and DTOs ↔ the ABP.io
        /// role ids (Guid). The role's old id is IdentityRole.LegacyId.
        /// </summary>
        {{MapClass("LegacyRoleIds", "int", "Role")}}

        """;

    private static string MapClass(string name, string key, string entity) => $$"""
        public static class {{name}}
        {
            private static readonly ConcurrentDictionary<{{key}}, Guid> Ids = new();
            private static readonly ConcurrentDictionary<Guid, {{key}}> LegacyIdsById = new();

            /// <summary>Set at application start by the EF Core layer: legacy id → {{entity.ToLowerInvariant()}} id, and back.</summary>
            public static Func<{{key}}, Guid?>? Resolver { get; set; }

            public static Func<Guid, {{key}}?>? ReverseResolver { get; set; }

            public static void Set({{key}} legacyId, Guid id)
            {
                Ids[legacyId] = id;
                LegacyIdsById[id] = legacyId;
            }

            /// <summary>The ABP.io id of a source {{entity.ToLowerInvariant()}} id (Guid.Empty when there is none).</summary>
            public static Guid To{{entity}}Guid({{key}} legacyId)
            {
                if (Ids.TryGetValue(legacyId, out var id)) return id;
                if (Resolver?.Invoke(legacyId) is not { } resolved) return Guid.Empty;
                Set(legacyId, resolved);
                return resolved;
            }

            /// <summary>null stays null.</summary>
            public static Guid? To{{entity}}Guid({{key}}? legacyId) => legacyId is null ? null : To{{entity}}Guid(legacyId.Value);

            /// <summary>The source id of an ABP.io {{entity.ToLowerInvariant()}} (0 when there is none).</summary>
            public static {{key}} ToLegacy{{entity}}Id(Guid id)
            {
                if (LegacyIdsById.TryGetValue(id, out var legacyId)) return legacyId;
                if (ReverseResolver?.Invoke(id) is not { } resolved) return 0;
                Set(resolved, id);
                return resolved;
            }

            /// <summary>null stays null.</summary>
            public static {{key}}? ToLegacy{{entity}}Id(Guid? id) => id is null ? null : ToLegacy{{entity}}Id(id.Value);
        }
        """;

    private static string DomainCode(string ns) => $$"""
        using System;
        using System.Linq;
        using System.Security.Claims;
        using System.Security.Principal;
        using System.Threading.Tasks;
        using Microsoft.Extensions.DependencyInjection;
        using Volo.Abp;
        using Volo.Abp.Data;
        using Volo.Abp.DependencyInjection;
        using Volo.Abp.Identity;
        using Volo.Abp.ObjectExtending;
        using Volo.Abp.Security.Claims;
        using Volo.Abp.Threading;
        using Volo.Abp.TenantManagement;
        using Volo.Abp.Users;

        namespace {{ns}};

        /// <summary>
        /// Generated by AbpToVolo (--id-type source): the application keeps the source key types of its references to users
        /// (long) and roles (int). The ABP.io entities keep their Guid keys and store the old id as LegacyId.
        /// </summary>
        public static class LegacyIds
        {
            public const string PropertyName = "LegacyId";
            public const string UserClaimType = "legacy_user_id";
            public const string UserSequence = "AbpUserLegacyIds";
            public const string RoleSequence = "AbpRoleLegacyIds";
            public const string TenantSequence = "AbpTenantLegacyIds";

            private static readonly OneTimeRunner OneTimeRunner = new();

            /// <summary>The LegacyId extension properties of IdentityUser (long), IdentityRole (int) and Tenant (int).</summary>
            public static void ConfigureExtensionProperties()
            {
                OneTimeRunner.Run(() =>
                {
                    ObjectExtensionManager.Instance.Modules().ConfigureIdentity(identity =>
                    {
                        identity.ConfigureUser(user => user.AddOrUpdateProperty<long>(PropertyName, property => property.UI.OnCreateForm.IsVisible = false));
                        identity.ConfigureRole(role => role.AddOrUpdateProperty<int>(PropertyName, property => property.UI.OnCreateForm.IsVisible = false));
                    });
                    ObjectExtensionManager.Instance.Modules().ConfigureTenantManagement(tenantManagement =>
                    {
                        tenantManagement.ConfigureTenant(tenant => tenant.AddOrUpdateProperty<int>(PropertyName, property => property.UI.OnCreateForm.IsVisible = false));
                    });
                });
            }

            public static long GetLegacyId(this IdentityUser user) => user.GetProperty<long>(PropertyName);

            public static int GetLegacyId(this IdentityRole role) => role.GetProperty<int>(PropertyName);

            public static int GetLegacyId(this Tenant tenant) => tenant.GetProperty<int>(PropertyName);

            /// <summary>The legacy (source) id of the current user, from the legacy_user_id claim; null when not logged in.</summary>
            public static long? GetLegacyId(this ICurrentUser currentUser) =>
                long.TryParse(currentUser.FindClaim(UserClaimType)?.Value, out var id) ? id : null;

            public static long GetRequiredLegacyId(this ICurrentUser currentUser) =>
                currentUser.GetLegacyId() ?? throw new AbpException("The current user has no legacy id (not logged in?).");
        }

        /// <summary>Finds the ABP.io users/roles by their legacy (source) id. Implemented in the EF Core layer.</summary>
        public interface ILegacyIdLookup
        {
            Task<IdentityUser?> FindUserAsync(long legacyId);

            Task<IdentityRole?> FindRoleAsync(int legacyId);

            Task<Tenant?> FindTenantAsync(int legacyId);

            Task<Guid?> FindUserIdAsync(long legacyId);
        }

        /// <summary>Adds the current user's legacy id as the legacy_user_id claim (login and dynamic claims).</summary>
        public class LegacyUserIdClaimsPrincipalContributor : IAbpClaimsPrincipalContributor, ITransientDependency
        {
            public async Task ContributeAsync(AbpClaimsPrincipalContributorContext context)
            {
                var identity = context.ClaimsPrincipal.Identities.FirstOrDefault();
                var userId = identity?.FindUserId();
                if (identity is null || userId is null) return;

                var user = await context.ServiceProvider.GetRequiredService<IIdentityUserRepository>().FindAsync(userId.Value);
                if (user is null) return;

                identity.AddOrReplace(new Claim(LegacyIds.UserClaimType, user.GetLegacyId().ToString()));
            }
        }

        """;

    private static string EfCoreCode(string domainNs, string ns) => $$"""
        using System;
        using System.ComponentModel.DataAnnotations.Schema;
        using System.Linq;
        using System.Reflection;
        using System.Threading.Tasks;
        using Microsoft.EntityFrameworkCore;
        using Microsoft.Extensions.DependencyInjection;
        using Volo.Abp.DependencyInjection;
        using Volo.Abp.Domain.Repositories;
        using Volo.Abp.Identity;
        using Volo.Abp.ObjectExtending;
        using Volo.Abp.Threading;
        using Volo.Abp.TenantManagement;
        using Volo.Abp.Uow;
        using {{domainNs}};

        namespace {{ns}};

        /// <summary>Generated by AbpToVolo (--id-type source): the LegacyId columns of AbpUsers/AbpRoles/AbpTenants.</summary>
        public static class LegacyIdsEfCoreMappings
        {
            private static readonly OneTimeRunner OneTimeRunner = new();

            public static void Configure()
            {
                LegacyIds.ConfigureExtensionProperties();
                OneTimeRunner.Run(() =>
                {
                    // Old users/roles keep their id (data script); new ones are numbered by the sequences.
                    ObjectExtensionManager.Instance.MapEfCoreProperty<IdentityUser, long>(LegacyIds.PropertyName,
                        (_, property) => property.HasDefaultValueSql($"NEXT VALUE FOR [{LegacyIds.UserSequence}]"));
                    ObjectExtensionManager.Instance.MapEfCoreProperty<IdentityRole, int>(LegacyIds.PropertyName,
                        (_, property) => property.HasDefaultValueSql($"NEXT VALUE FOR [{LegacyIds.RoleSequence}]"));
                    ObjectExtensionManager.Instance.MapEfCoreProperty<Tenant, int>(LegacyIds.PropertyName,
                        (_, property) => property.HasDefaultValueSql($"NEXT VALUE FOR [{LegacyIds.TenantSequence}]"));
                });
            }
        }

        public static class LegacyIdsModelBuilderExtensions
        {
            /// <summary>
            /// The sequences of the legacy ids, and HasPrincipalKey("LegacyId") for every navigation of the application's
            /// entities to IdentityUser / IdentityRole whose foreign key has the source type (long / int).
            /// </summary>
            public static void ConfigureLegacyIds(this ModelBuilder builder, bool useSequences = true)
            {
                // Declared here too (not only by the extension mapping): "dotnet ef" does not run the modules at design time.
                var userLegacyId = builder.Entity<IdentityUser>().Property<long>(LegacyIds.PropertyName);
                var roleLegacyId = builder.Entity<IdentityRole>().Property<int>(LegacyIds.PropertyName);
                var hasTenants = builder.Model.FindEntityType(typeof(Tenant)) is not null;
                var tenantLegacyId = hasTenants ? builder.Entity<Tenant>().Property<int>(LegacyIds.PropertyName) : null;
                if (useSequences)
                {
                    builder.HasSequence<long>(LegacyIds.UserSequence);
                    builder.HasSequence<int>(LegacyIds.RoleSequence);
                    userLegacyId.HasDefaultValueSql($"NEXT VALUE FOR [{LegacyIds.UserSequence}]");
                    roleLegacyId.HasDefaultValueSql($"NEXT VALUE FOR [{LegacyIds.RoleSequence}]");
                    if (tenantLegacyId is not null)
                    {
                        builder.HasSequence<int>(LegacyIds.TenantSequence);
                        tenantLegacyId.HasDefaultValueSql($"NEXT VALUE FOR [{LegacyIds.TenantSequence}]");
                    }
                }
                else
                {
                    // Providers without sequences (SQLite tests): unique numbers generated in memory.
                    builder.Entity<IdentityUser>().Property<long>(LegacyIds.PropertyName).HasValueGenerator<LegacyIdValueGenerator>();
                    builder.Entity<IdentityRole>().Property<int>(LegacyIds.PropertyName).HasValueGenerator<LegacyRoleIdValueGenerator>();
                    tenantLegacyId?.HasValueGenerator<LegacyRoleIdValueGenerator>();
                }

                foreach (var entityType in builder.Model.GetEntityTypes().Where(t => !t.IsOwned() && t.ClrType.Namespace?.StartsWith("Volo.", StringComparison.Ordinal) != true).ToList())
                {
                    var clr = entityType.ClrType;
                    foreach (var navigation in clr.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                 .Where(p => p.PropertyType == typeof(IdentityUser) || p.PropertyType == typeof(IdentityRole) || p.PropertyType == typeof(Tenant)))
                    {
                        var foreignKey = ForeignKeyOf(clr, navigation);
                        var keyType = foreignKey is null ? null : Nullable.GetUnderlyingType(foreignKey.PropertyType) ?? foreignKey.PropertyType;
                        if (keyType != typeof(long) && keyType != typeof(int)) continue;

                        builder.Entity(clr)
                            .HasOne(navigation.PropertyType, navigation.Name)
                            .WithMany()
                            .HasForeignKey(foreignKey!.Name)
                            .HasPrincipalKey(LegacyIds.PropertyName);
                    }
                }
            }

            /// <summary>[ForeignKey("X")] on the navigation, [ForeignKey("Nav")] on the key, or the {Navigation}Id convention.</summary>
            private static PropertyInfo? ForeignKeyOf(Type entity, PropertyInfo navigation)
            {
                if (navigation.GetCustomAttribute<ForeignKeyAttribute>()?.Name is { } name) return entity.GetProperty(name.Split(',')[0].Trim());
                var attributed = entity.GetProperties().FirstOrDefault(p => p.GetCustomAttribute<ForeignKeyAttribute>()?.Name?.Trim() == navigation.Name);
                return attributed ?? entity.GetProperty(navigation.Name + "Id");
            }
        }

        public class LegacyIdValueGenerator : Microsoft.EntityFrameworkCore.ValueGeneration.ValueGenerator<long>
        {
            private static long _last = 1_000_000_000;

            public override bool GeneratesTemporaryValues => false;

            public override long Next(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry) => System.Threading.Interlocked.Increment(ref _last);
        }

        public class LegacyRoleIdValueGenerator : Microsoft.EntityFrameworkCore.ValueGeneration.ValueGenerator<int>
        {
            private static int _last = 1_000_000;

            public override bool GeneratesTemporaryValues => false;

            public override int Next(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry) => System.Threading.Interlocked.Increment(ref _last);
        }

        /// <summary>Resolves LegacyTenantIds / LegacyUserIds (source id ↔ ABP.io id) from the AbpTenants / AbpUsers tables.</summary>
        public static class LegacyIdMapsResolver
        {
            public static void Initialize(IServiceProvider serviceProvider)
            {
                // The initialization context gives a scope that is disposed after startup: keep the root provider.
                serviceProvider = serviceProvider.GetRequiredService<IRootServiceProvider>();
                LegacyTenantIds.Resolver = legacyId => Query<Tenant, Guid?>(serviceProvider, async tenants =>
                    await (await tenants.GetQueryableAsync()).Where(t => EF.Property<int>(t, LegacyIds.PropertyName) == legacyId)
                        .Select(t => (Guid?)t.Id).FirstOrDefaultAsync());
                LegacyTenantIds.ReverseResolver = id => Query<Tenant, int?>(serviceProvider, async tenants =>
                    await (await tenants.GetQueryableAsync()).Where(t => t.Id == id)
                        .Select(t => (int?)EF.Property<int>(t, LegacyIds.PropertyName)).FirstOrDefaultAsync());
                LegacyUserIds.Resolver = legacyId => Query<IdentityUser, Guid?>(serviceProvider, async users =>
                    await (await users.GetQueryableAsync()).Where(u => EF.Property<long>(u, LegacyIds.PropertyName) == legacyId)
                        .Select(u => (Guid?)u.Id).FirstOrDefaultAsync());
                LegacyUserIds.ReverseResolver = id => Query<IdentityUser, long?>(serviceProvider, async users =>
                    await (await users.GetQueryableAsync()).Where(u => u.Id == id)
                        .Select(u => (long?)EF.Property<long>(u, LegacyIds.PropertyName)).FirstOrDefaultAsync());
                LegacyRoleIds.Resolver = legacyId => Query<IdentityRole, Guid?>(serviceProvider, async roles =>
                    await (await roles.GetQueryableAsync()).Where(r => EF.Property<int>(r, LegacyIds.PropertyName) == legacyId)
                        .Select(r => (Guid?)r.Id).FirstOrDefaultAsync());
                LegacyRoleIds.ReverseResolver = id => Query<IdentityRole, int?>(serviceProvider, async roles =>
                    await (await roles.GetQueryableAsync()).Where(r => r.Id == id)
                        .Select(r => (int?)EF.Property<int>(r, LegacyIds.PropertyName)).FirstOrDefaultAsync());
            }

            /// <summary>A query in its own unit of work, over every tenant (the source ids are unique in the whole database).</summary>
            private static TResult Query<TEntity, TResult>(IServiceProvider serviceProvider, Func<IRepository<TEntity, Guid>, Task<TResult>> query)
                where TEntity : class, Volo.Abp.Domain.Entities.IEntity<Guid>
            {
                using var scope = serviceProvider.CreateScope();
                using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true, isTransactional: false);
                using var allTenants = scope.ServiceProvider.GetRequiredService<Volo.Abp.Data.IDataFilter>().Disable<Volo.Abp.MultiTenancy.IMultiTenant>();
                var result = AsyncHelper.RunSync(() => query(scope.ServiceProvider.GetRequiredService<IRepository<TEntity, Guid>>()));
                AsyncHelper.RunSync(() => uow.CompleteAsync());
                return result;
            }
        }

        /// <summary>EF Core implementation of ILegacyIdLookup.</summary>
        public class EfCoreLegacyIdLookup : ILegacyIdLookup, ITransientDependency
        {
            private readonly IRepository<IdentityUser, Guid> _users;
            private readonly IRepository<IdentityRole, Guid> _roles;
            private readonly IRepository<Tenant, Guid> _tenants;

            public EfCoreLegacyIdLookup(IRepository<IdentityUser, Guid> users, IRepository<IdentityRole, Guid> roles, IRepository<Tenant, Guid> tenants)
            {
                _users = users;
                _roles = roles;
                _tenants = tenants;
            }

            public async Task<IdentityUser?> FindUserAsync(long legacyId) =>
                await (await _users.GetQueryableAsync()).FirstOrDefaultAsync(u => EF.Property<long>(u, LegacyIds.PropertyName) == legacyId);

            public async Task<IdentityRole?> FindRoleAsync(int legacyId) =>
                await (await _roles.GetQueryableAsync()).FirstOrDefaultAsync(r => EF.Property<int>(r, LegacyIds.PropertyName) == legacyId);

            public async Task<Tenant?> FindTenantAsync(int legacyId) =>
                await (await _tenants.GetQueryableAsync()).FirstOrDefaultAsync(t => EF.Property<int>(t, LegacyIds.PropertyName) == legacyId);

            public async Task<Guid?> FindUserIdAsync(long legacyId) => (await FindUserAsync(legacyId))?.Id;
        }

        """;
}
