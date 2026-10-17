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
        FileSystem.WriteText(efFile, EfCoreCode(ns, efNs));

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

    internal static string EfCoreCode(string ns, string efNs)
    {
        var properties = string.Concat(ZeroMappings.MissingEntityColumns.SelectMany(e => e.Value.Select(c =>
            $"{Environment.NewLine}        builder.Entity<{e.Key}>().Property<{c.Type}>(\"{c.Name}\");")));
        var shadowNames = string.Join(", ", ZeroMappings.MissingEntityColumns.SelectMany(e => e.Value.Select(c => $"\"{c.Name}\"")).Distinct());
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
