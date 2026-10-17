using Microsoft.CodeAnalysis;
using AbpToVolo.CodeMigration;
using AbpToVolo.Core;
using AbpToVolo.Mappings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.Steps;

/// <summary>
/// Module Zero columns of entities ABP.io cannot extend (IdentityUserToken has no ExtraProperties): UserToken.ExpireDate…
/// They stay in the table as EF Core shadow properties. The code reads them in queries with EF.Property and elsewhere with
/// EntityShadowProperties.Get / Set (compiler-guided fixes); an EF Core interceptor keeps EntityShadowProperties and the
/// shadow properties in sync (loaded entities → values, saved entities ← values).
/// </summary>
public sealed class ZeroColumnsStep : IMigrationStep
{
    public string Name => "Module Zero columns (shadow properties)";

    public bool IsEnabled(MigrationOptions options) => options.ConvertModuleZero;

    public void Execute(MigrationContext context)
    {
        if (!context.UsesIdentityModules) return;
        var efCore = context.FindProject(ProjectLayer.EntityFrameworkCore);
        var shared = context.FindProject(ProjectLayer.DomainShared) ?? context.FindProject(ProjectLayer.Domain);
        if (efCore is null || shared is null) return;

        var ns = context.SolutionName is { } solution && shared.RootNamespace.StartsWith(solution, StringComparison.Ordinal) ? solution : shared.RootNamespace;
        var efNs = efCore.RootNamespace + ".EntityFrameworkCore";

        var storeFile = Path.Combine(shared.Directory, CompatShimStep.ShimFolder, "EntityShadowProperties.cs");
        FileSystem.WriteText(storeFile, StoreCode(ns));
        var efFile = Path.Combine(efCore.Directory, "EntityFrameworkCore", "ModuleZeroColumns.cs");
        var permissionSettings = AddPermissionSettings(context, efCore, ns, efNs);
        FileSystem.WriteText(efFile, EfCoreCode(ns, efNs, permissionSettings));

        if (AbpModulesStep.FindModuleFile(efCore, AbpModulesStep.ModuleNameFilters[ProjectLayer.EntityFrameworkCore]) is { } module)
        {
            var text = File.ReadAllText(module);
            var updated = ModuleMethodEditor.AddStatement(text, AbpModulesStep.ModuleNameFilters[ProjectLayer.EntityFrameworkCore], "ConfigureServices",
                "ServiceConfigurationContext context", "ModuleZeroColumns.ConfigureInterceptor(context.Services);");
            if (updated is not null && updated != text) FileSystem.WriteText(module, ModuleMethodEditor.AddUsings(updated, [efNs]));
        }

        if (AddModelBuilderCall(context, efCore, efNs) is null)
        {
            context.Report.Todo(Name, "Call builder.ConfigureModuleZeroColumns() at the end of OnModelCreating of the application DbContext.", context.Relative(efFile));
        }

        var columns = string.Join(", ", ZeroMappings.MissingEntityColumns.SelectMany(e => e.Value.Select(c => $"{e.Key}.{c.Name}")));
        context.Report.Change(Name, $"Module Zero columns kept as shadow properties ({columns}), read and written with EntityShadowProperties", context.Relative(efFile));
    }

    private static readonly string[] PermissionSettingTypes = ["PermissionSetting", "UserPermissionSetting", "RolePermissionSetting"];

    /// <summary>
    /// Module Zero's permission settings (UserPermissionSetting, RolePermissionSetting, queried by UserId / RoleId / Name /
    /// IsGranted) have no ABP.io counterpart with the same shape: PermissionGrant has ProviderName + ProviderKey (user id,
    /// role name) and only granted rows. The application's queries keep working on read-only entities of the same names,
    /// mapped (ToSqlQuery) over AbpPermissionGrants with the users'/roles' source ids. Changes go through ABP.io's
    /// IPermissionManager.
    /// </summary>
    private bool AddPermissionSettings(MigrationContext context, ProjectInfo efCore, string ns, string efNs)
    {
        if (context.FindProject(ProjectLayer.Domain) is not { } domain) return false;
        var used = context.Projects.Any(p => FileSystem.EnumerateFiles(p.Directory, "*.cs").Where(f => !context.IsExcluded(f))
            .Any(f => PermissionSettingTypes.Any(t => File.ReadAllText(f).Contains(t, StringComparison.Ordinal))));
        if (!used) return false;

        var legacyIds = context.Options.IdType == IdTypeMode.Source;
        var entityFile = Path.Combine(domain.Directory, CompatShimStep.ShimFolder, "PermissionSettings.cs");
        FileSystem.WriteText(entityFile, PermissionSettingsCode(ns, legacyIds));
        var efFile = Path.Combine(efCore.Directory, "EntityFrameworkCore", "ModuleZeroPermissionSettings.cs");
        FileSystem.WriteText(efFile, PermissionSettingsEfCoreCode(ns, efNs, legacyIds));

        // Default repositories are registered for the DbSet properties of the DbContext.
        if (AddDbSets(context, efCore, ns) is null)
        {
            context.Report.Todo(Name, "Add DbSet<PermissionSetting>, DbSet<UserPermissionSetting> and DbSet<RolePermissionSetting> properties to the application DbContext.",
                context.Relative(efFile));
        }

        context.Report.Change(Name, "Module Zero permission settings (UserPermissionSetting, RolePermissionSetting) kept as read-only entities over AbpPermissionGrants",
            context.Relative(entityFile));
        context.Report.Todo(Name, "UserPermissionSetting/RolePermissionSetting are read-only views of AbpPermissionGrants (granted permissions only); " +
                                  "grant or revoke permissions with IPermissionManager.", context.Relative(entityFile));
        return true;
    }

    private static string? AddDbSets(MigrationContext context, ProjectInfo efCore, string ns)
    {
        foreach (var file in FileSystem.EnumerateFiles(efCore.Directory, "*.cs").Where(f => !context.IsExcluded(f)))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("AbpDbContext<", StringComparison.Ordinal)) continue;

            var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
            var cls = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => SyntaxHelpers.HasBaseType(c, "AbpDbContext"));
            if (cls is null) continue;
            var missing = PermissionSettingTypes.Where(t => !cls.Members.OfType<PropertyDeclarationSyntax>().Any(p => p.Type.ToString() == $"DbSet<{t}>")).ToList();
            if (missing.Count == 0) return file;

            var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var indent = cls.Members.Count > 0 ? SyntaxHelpers.Indentation(cls.Members[0]) : SyntaxHelpers.Indentation(cls) + "    ";
            var members = missing.Select((t, i) => (MemberDeclarationSyntax)ParseMemberDeclaration($"public DbSet<{t}> {t}s {{ get; set; }}")!
                .WithLeadingTrivia(i == 0
                    ? TriviaList(Whitespace(indent), Comment("/* AbpToVolo: Module Zero permission settings, read-only over AbpPermissionGrants */"), EndOfLine(nl), Whitespace(indent))
                    : TriviaList(Whitespace(indent)))
                .WithTrailingTrivia(EndOfLine(nl)));
            var updated = root.ReplaceNode(cls, cls.WithMembers(cls.Members.InsertRange(0, members)));
            FileSystem.WriteText(file, ModuleMethodEditor.AddUsings(updated.ToFullString(), [ns, "Microsoft.EntityFrameworkCore"]), FileSystem.ReadText(file).Encoding);
            return file;
        }

        return null;
    }

    internal static string PermissionSettingsCode(string ns, bool legacyIds) => $$"""
        using System;
        using Volo.Abp.Domain.Entities;
        using Volo.Abp.MultiTenancy;

        namespace {{ns}};

        /// <summary>
        /// Generated by AbpToVolo: Module Zero's permission settings, read-only, over ABP.io's AbpPermissionGrants
        /// (ModuleZeroPermissionSettings). Every row is a granted permission; grant or revoke with IPermissionManager.
        /// The Id is a row number of the query (stable while the grants do not change).
        /// </summary>
        public abstract class PermissionSetting : Entity<long>, IMultiTenant
        {
            public virtual Guid? TenantId { get; set; }

            public virtual string Name { get; set; } = null!;

            public virtual bool IsGranted { get; set; } = true;
        }

        /// <summary>Generated by AbpToVolo: a permission granted to a user (ProviderName "U").</summary>
        public class UserPermissionSetting : PermissionSetting
        {
            public virtual {{(legacyIds ? "long" : "Guid")}} UserId { get; set; }
        }

        /// <summary>Generated by AbpToVolo: a permission granted to a role (ProviderName "R", ProviderKey = role name).</summary>
        public class RolePermissionSetting : PermissionSetting
        {
            public virtual {{(legacyIds ? "int" : "Guid")}} RoleId { get; set; }
        }

        """;

    internal static string PermissionSettingsEfCoreCode(string ns, string efNs, bool legacyIds)
    {
        var userId = legacyIds ? "u.LegacyId" : "u.Id";
        var roleId = legacyIds ? "r.LegacyId" : "r.Id";
        var noUser = legacyIds ? "CAST(NULL AS bigint)" : "CAST(NULL AS uniqueidentifier)";
        var noRole = legacyIds ? "CAST(NULL AS int)" : "CAST(NULL AS uniqueidentifier)";
        return $$""""
            using Microsoft.EntityFrameworkCore;
            using Volo.Abp.Identity;
            using Volo.Abp.PermissionManagement;
            using {{ns}};

            namespace {{efNs}};

            /// <summary>
            /// Generated by AbpToVolo: Module Zero's permission settings as read-only entities over AbpPermissionGrants
            /// (SQL Server): user grants with the user's {{(legacyIds ? "source id (LegacyId)" : "id")}}, role grants (ProviderKey = role name) with the
            /// role's {{(legacyIds ? "source id (LegacyId)" : "id")}}.
            /// </summary>
            public static class ModuleZeroPermissionSettings
            {
                public static void ConfigureModuleZeroPermissionSettings(this ModelBuilder builder)
                {
                    var grants = AbpPermissionManagementDbProperties.DbTablePrefix + "PermissionGrants";
                    var users = AbpIdentityDbProperties.DbTablePrefix + "Users";
                    var roles = AbpIdentityDbProperties.DbTablePrefix + "Roles";
                    builder.Entity<PermissionSetting>(b =>
                    {
                        b.ToTable((string?)null); // no table (the DbSet would map one): not in migrations
                        b.ToSqlQuery($"""
                            SELECT CAST(ROW_NUMBER() OVER (ORDER BY s.GrantId) AS bigint) AS Id, s.TenantId, s.Name, CAST(1 AS bit) AS IsGranted,
                                   s.Discriminator, s.UserId, s.RoleId
                            FROM (
                                SELECT g.Id AS GrantId, g.TenantId, g.Name, 'UserPermissionSetting' AS Discriminator, {{userId}} AS UserId, {{noRole}} AS RoleId
                                FROM {grants} g JOIN {users} u ON u.Id = TRY_CONVERT(uniqueidentifier, g.ProviderKey)
                                WHERE g.ProviderName = 'U'
                                UNION ALL
                                SELECT g.Id, g.TenantId, g.Name, 'RolePermissionSetting', {{noUser}}, {{roleId}}
                                FROM {grants} g JOIN {roles} r ON r.Name = g.ProviderKey AND (r.TenantId = g.TenantId OR (r.TenantId IS NULL AND g.TenantId IS NULL))
                                WHERE g.ProviderName = 'R'
                            ) s
                            """);
                        b.HasDiscriminator<string>("Discriminator")
                            .HasValue<UserPermissionSetting>("UserPermissionSetting")
                            .HasValue<RolePermissionSetting>("RolePermissionSetting");
                    });
                }
            }

            """";
    }

    /// <summary>builder.ConfigureModuleZeroColumns() at the end of OnModelCreating of the application DbContext.</summary>
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
            if (method.Body!.ToString().Contains("ConfigureModuleZeroColumns", StringComparison.Ordinal)) return file;

            var builder = method.ParameterList.Parameters[0].Identifier.Text;
            var indent = method.Body.Statements.Count > 0 ? SyntaxHelpers.Indentation(method.Body.Statements[0]) : SyntaxHelpers.Indentation(method) + "    ";
            var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var call = ParseStatement($"{builder}.ConfigureModuleZeroColumns();")
                .WithLeadingTrivia(Whitespace(indent), Comment("/* AbpToVolo: Module Zero columns ABP.io entities do not have (UserToken.ExpireDate…), as shadow properties */"),
                    EndOfLine(nl), Whitespace(indent))
                .WithTrailingTrivia(EndOfLine(nl));
            var updated = root.ReplaceNode(method.Body, method.Body.WithStatements(method.Body.Statements.Add(call)));
            FileSystem.WriteText(file, ModuleMethodEditor.AddUsings(updated.ToFullString(), [ns]), FileSystem.ReadText(file).Encoding);
            return file;
        }

        return null;
    }

    internal static string StoreCode(string ns) => $$"""
        using System;
        using System.Collections.Generic;
        using System.Runtime.CompilerServices;

        namespace {{ns}};

        /// <summary>
        /// Generated by AbpToVolo: values of Module Zero columns that the ABP.io entities do not have as properties
        /// (IdentityUserToken.ExpireDate…). They are EF Core shadow properties: loaded with the entity and saved with it
        /// (ModuleZeroColumns interceptor); in queries use EF.Property&lt;T&gt;(entity, "Name").
        /// </summary>
        public static class EntityShadowProperties
        {
            private static readonly ConditionalWeakTable<object, Dictionary<string, object?>> Values = new();

            public static T? Get<T>(object entity, string name)
            {
                lock (Values)
                {
                    return Values.TryGetValue(entity, out var values) && values.TryGetValue(name, out var value) && value is T typed ? typed : default;
                }
            }

            public static void Set(object entity, string name, object? value)
            {
                lock (Values)
                {
                    Values.GetOrCreateValue(entity)[name] = value;
                }
            }

            /// <summary>The values set on an entity (for the EF Core interceptor).</summary>
            public static IReadOnlyDictionary<string, object?>? GetAll(object entity)
            {
                lock (Values)
                {
                    return Values.TryGetValue(entity, out var values) ? new Dictionary<string, object?>(values) : null;
                }
            }
        }

        """;

    internal static string EfCoreCode(string ns, string efNs, bool permissionSettings = false)
    {
        var properties = string.Concat(ZeroMappings.MissingEntityColumns.SelectMany(e => e.Value.Select(c =>
            $"{Environment.NewLine}        builder.Entity<{e.Key}>().Property<{c.Type}>(\"{c.Name}\");")));
        var shadowNames = string.Join(", ", ZeroMappings.MissingEntityColumns.SelectMany(e => e.Value.Select(c => $"\"{c.Name}\"")).Distinct());
        if (permissionSettings) properties += $"{Environment.NewLine}        builder.ConfigureModuleZeroPermissionSettings();";
        return $$"""
            using System;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Diagnostics;
            using Microsoft.Extensions.DependencyInjection;
            using Volo.Abp.EntityFrameworkCore;
            using Volo.Abp.Identity;
            using {{ns}};

            namespace {{efNs}};

            /// <summary>Generated by AbpToVolo: Module Zero columns the ABP.io entities do not have, kept as shadow properties.</summary>
            public static class ModuleZeroColumns
            {
                private static readonly string[] Names = [{{shadowNames}}];

                // One instance for every DbContext (a new one each time would make EF Core build a new internal service provider).
                private static readonly Interceptor SharedInterceptor = new();

                public static void ConfigureModuleZeroColumns(this ModelBuilder builder)
                {{{properties}}
                }

                public static void ConfigureInterceptor(IServiceCollection services) =>
                    services.Configure<AbpDbContextOptions>(options => options.PreConfigure(context => context.DbContextOptions.AddInterceptors(SharedInterceptor)));

                /// <summary>Loaded entities → EntityShadowProperties; saved entities ← EntityShadowProperties.</summary>
                private sealed class Interceptor : SaveChangesInterceptor, IMaterializationInterceptor
                {
                    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
                    {
                        foreach (var property in materializationData.EntityType.GetProperties().Where(p => p.IsShadowProperty() && Names.Contains(p.Name)))
                        {
                            EntityShadowProperties.Set(entity, property.Name, materializationData.GetPropertyValue(property));
                        }

                        return entity;
                    }

                    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
                    {
                        Apply(eventData.Context);
                        return result;
                    }

                    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
                        CancellationToken cancellationToken = default)
                    {
                        Apply(eventData.Context);
                        return ValueTask.FromResult(result);
                    }

                    private static void Apply(DbContext? context)
                    {
                        if (context is null) return;
                        foreach (var entry in context.ChangeTracker.Entries())
                        {
                            if (EntityShadowProperties.GetAll(entry.Entity) is not { } values) continue;
                            foreach (var (name, value) in values)
                            {
                                if (entry.Metadata.FindProperty(name) is null) continue;
                                var property = entry.Property(name);
                                if (!Equals(property.CurrentValue, value)) property.CurrentValue = value;
                            }
                        }
                    }
                }
            }

            """;
    }
}
