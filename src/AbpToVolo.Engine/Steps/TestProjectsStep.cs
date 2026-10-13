using System.Text;
using System.Text.RegularExpressions;
using AbpToVolo.CodeMigration;
using AbpToVolo.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AbpToVolo.Steps;

/// <summary>
/// Generates the test projects of the ABP.io layered template:
///   test/X.TestBase                   — XTestBaseModule (Autofac, always-allow authorization, data seeding) and XTestBase&lt;TModule&gt;
///   test/X.Domain.Tests               — XDomainTestModule + abstract tests for every domain service
///   test/X.Application.Tests          — XApplicationTestModule + abstract tests for every application service
///   test/X.EntityFrameworkCore.Tests  — SQLite in-memory test module, a query test for every DbSet and the concrete
///                                        (EF Core) versions of the abstract domain/application tests
/// As in the ABP.io template, domain/application tests are abstract and database independent; the EF Core project
/// runs them against an in-memory SQLite database.
/// </summary>
public sealed partial class TestProjectsStep : IMigrationStep
{
    internal static readonly (string Name, string Version)[] TestPackages =
    [
        ("Microsoft.NET.Test.Sdk", "18.10.1"),
        ("xunit", "2.9.3"),
        ("xunit.runner.visualstudio", "3.1.0"),
        ("Shouldly", "4.3.0"),
        ("NSubstitute", "5.3.0"),
    ];

    private sealed record ModuleInfo(string FullName);

    private sealed record AppServiceInfo(string Interface, string? GetListInput);

    public string Name => "Test projects";

    public bool IsEnabled(MigrationOptions options) => options.CreateTests;

    public void Execute(MigrationContext context)
    {
        var domain = context.FindProject(ProjectLayer.Domain);
        var application = context.FindProject(ProjectLayer.Application);
        var efCore = context.FindProject(ProjectLayer.EntityFrameworkCore);
        if (domain is null || application is null || efCore is null)
        {
            context.Report.Warning(Name, "Domain, Application or EntityFrameworkCore project not found; test projects were not generated.");
            return;
        }

        var files = context.Projects.Where(p => p.Layer != ProjectLayer.Tests)
            .SelectMany(p => FileSystem.EnumerateFiles(p.Directory, "*.cs"))
            .Where(f => !context.IsExcluded(f))
            .ToList();
        var model = SolutionModel.Build(files);

        var domainModule = FindModule(model, context, domain, n => n.EndsWith("CoreModule", StringComparison.Ordinal) || n.EndsWith("DomainModule", StringComparison.Ordinal));
        var applicationModule = FindModule(model, context, application, n => n.EndsWith("ApplicationModule", StringComparison.Ordinal));
        var efModule = FindModule(model, context, efCore, n => n.EndsWith("Module", StringComparison.Ordinal));
        var dbContext = FindDbContext(model, context, efCore);
        if (domainModule is null || applicationModule is null || efModule is null || dbContext is null)
        {
            context.Report.Warning(Name, "Module classes or the DbContext could not be found; test projects were not generated.");
            return;
        }

        var rootNs = domain.RootNamespace;
        var prefix = new string(context.ModulePrefix.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        var baseName = ContractsScaffoldStep.BaseName(domain);
        var testRoot = Path.Combine(context.OutputRoot, "test");
        var solution = ProjectWriter.FindSolution(context.OutputRoot);
        var tfm = context.Options.TargetFramework;
        var abp = context.Options.AbpVersion;
        var testProperties = new Dictionary<string, string> { ["IsPackable"] = "false" };

        // ---- TestBase
        var testBaseDir = Path.Combine(testRoot, baseName + ".TestBase");
        var testBase = ProjectWriter.CreateProject(testBaseDir, baseName + ".TestBase", tfm, rootNs, [domain.CsprojPath],
            TestPackages.Concat([("Volo.Abp.TestBase", abp), ("Volo.Abp.Autofac", abp), ("Volo.Abp.Authorization", abp), ("Volo.Abp.BackgroundJobs.Abstractions", abp)]),
            properties: testProperties);
        Write(testBaseDir, $"{prefix}TestBaseModule.cs", TestBaseModule(rootNs, prefix));
        Write(testBaseDir, $"{prefix}TestBase.cs", TestBaseClass(rootNs, prefix));
        Write(testBaseDir, $"{prefix}TestDataSeedContributor.cs", SeedContributor(rootNs, prefix));
        Write(testBaseDir, $"{prefix}TestConsts.cs", TestConsts(rootNs, prefix));
        Write(testBaseDir, "Security/FakeCurrentPrincipalAccessor.cs", FakeCurrentPrincipalAccessor(rootNs));
        var identity = context.UsesIdentityModules;

        // ---- Domain.Tests
        var domainServices = FindDomainServices(model, context, domain);
        var domainTestsDir = Path.Combine(testRoot, baseName + ".Domain.Tests");
        var domainTests = ProjectWriter.CreateProject(domainTestsDir, baseName + ".Domain.Tests", tfm, rootNs, [domain.CsprojPath, testBase], TestPackages, properties: testProperties);
        Write(domainTestsDir, $"{prefix}DomainTestModule.cs", SimpleModule(rootNs, $"{prefix}DomainTestModule", domainModule.FullName, $"{prefix}TestBaseModule"));
        Write(domainTestsDir, $"{prefix}DomainTestBase.cs", AbstractBase(rootNs, $"{prefix}DomainTestBase", $"{prefix}TestBase"));
        Write(domainTestsDir, $"DomainServices/{prefix}DomainServiceTests.cs", DomainServiceTests(rootNs, prefix, domainServices));
        if (identity) Write(domainTestsDir, "Samples/SampleDomainTests.cs", SampleDomainTests(rootNs, prefix));

        // ---- Application.Tests
        var appServices = FindAppServices(model, context);
        var appTestsDir = Path.Combine(testRoot, baseName + ".Application.Tests");
        var appTests = ProjectWriter.CreateProject(appTestsDir, baseName + ".Application.Tests", tfm, rootNs, [application.CsprojPath, domainTests], TestPackages, properties: testProperties);
        Write(appTestsDir, $"{prefix}ApplicationTestModule.cs", SimpleModule(rootNs, $"{prefix}ApplicationTestModule", applicationModule.FullName, $"{prefix}DomainTestModule"));
        Write(appTestsDir, $"{prefix}ApplicationTestBase.cs", AbstractBase(rootNs, $"{prefix}ApplicationTestBase", $"{prefix}TestBase"));
        Write(appTestsDir, $"Services/{prefix}AppServiceTests.cs", AppServiceTests(rootNs, prefix, appServices));
        if (identity) Write(appTestsDir, "Samples/SampleAppServiceTests.cs", SampleAppServiceTests(rootNs, prefix));

        // ---- EntityFrameworkCore.Tests
        var (dbSets, skippedSets) = FindDbSets(model, context, efCore, dbContext);
        var efTestsDir = Path.Combine(testRoot, baseName + ".EntityFrameworkCore.Tests");
        // Spatial columns (NetTopologySuite): SQLite needs the NetTopologySuite plugin (with SpatiaLite) as well.
        var spatial = File.ReadAllText(efCore.CsprojPath).Contains("NetTopologySuite", StringComparison.OrdinalIgnoreCase);
        var efTestPackages = TestPackages.Concat([("Volo.Abp.EntityFrameworkCore.Sqlite", abp)]);
        if (spatial) efTestPackages = efTestPackages.Concat([("Microsoft.EntityFrameworkCore.Sqlite.NetTopologySuite", context.MicrosoftPackagesVersion)]);
        var efTests = ProjectWriter.CreateProject(efTestsDir, baseName + ".EntityFrameworkCore.Tests", tfm, rootNs, [efCore.CsprojPath, appTests],
            efTestPackages, properties: testProperties);
        Write(efTestsDir, $"EntityFrameworkCore/{prefix}EntityFrameworkCoreTestModule.cs",
            EfTestModule(rootNs, prefix, efModule.FullName, dbContext.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), identity, spatial));
        Write(efTestsDir, $"EntityFrameworkCore/{prefix}EntityFrameworkCoreTestBase.cs", EfTestBase(rootNs, prefix));
        Write(efTestsDir, "EntityFrameworkCore/SqliteTestModelCustomizer.cs", SqliteModelCustomizer(rootNs));
        Write(efTestsDir, $"EntityFrameworkCore/{prefix}EntityFrameworkCoreFixture.cs", EfFixture(rootNs, prefix));
        Write(efTestsDir, $"EntityFrameworkCore/{prefix}DbContextTests.cs", DbContextTests(rootNs, prefix, dbContext.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), dbSets));
        Write(efTestsDir, $"EntityFrameworkCore/Domains/EfCore{prefix}DomainServiceTests.cs", ConcreteTests(rootNs, $"EfCore{prefix}DomainServiceTests", $"{prefix}DomainServiceTests", prefix, $"{rootNs}.DomainServices"));
        Write(efTestsDir, $"EntityFrameworkCore/Applications/EfCore{prefix}AppServiceTests.cs", ConcreteTests(rootNs, $"EfCore{prefix}AppServiceTests", $"{prefix}AppServiceTests", prefix, $"{rootNs}.Services"));
        if (identity)
        {
            Write(efTestsDir, "EntityFrameworkCore/Domains/EfCoreSampleDomainTests.cs", ConcreteTests(rootNs, "EfCoreSampleDomainTests", "SampleDomainTests", prefix, $"{rootNs}.Samples"));
            Write(efTestsDir, "EntityFrameworkCore/Applications/EfCoreSampleAppServiceTests.cs", ConcreteTests(rootNs, "EfCoreSampleAppServiceTests", "SampleAppServiceTests", prefix, $"{rootNs}.Samples"));
            Write(efTestsDir, "EntityFrameworkCore/Samples/SampleRepositoryTests.cs", SampleRepositoryTests(rootNs, prefix));
        }

        foreach (var project in new[] { testBase, domainTests, appTests, efTests })
        {
            context.Report.Change(Name, $"Generated {Path.GetFileNameWithoutExtension(project)}", context.Relative(project));
            if (solution is not null && ProjectWriter.AddToSolution(solution, project, "test") is { } error)
            {
                context.Report.Warning(Name, $"Could not add {Path.GetFileName(project)} to the solution: {error}");
            }
        }

        var lines = context.Report.Section("Generated tests");
        lines.Add($"- `{baseName}.TestBase`: test module (Autofac, always-allow authorization, background jobs off, data seeding) and `{prefix}TestBase<TModule>`.");
        lines.Add($"- `{baseName}.Domain.Tests`: {domainServices.Count} domain service resolution tests (abstract).");
        lines.Add($"- `{baseName}.Application.Tests`: {appServices.Count} application service tests (abstract; {appServices.Count(a => a.GetListInput is not null)} also call GetListAsync).");
        lines.Add($"- `{baseName}.EntityFrameworkCore.Tests`: SQLite in-memory module, {dbSets.Count} DbSet query tests, and the concrete EF Core runs of the tests above.");
        if (skippedSets.Count > 0)
        {
            lines.Add($"- {skippedSets.Count} DbSets mapped to views/raw SQL were skipped (SQLite CreateTables does not create them): `{string.Join("`, `", skippedSets.Take(10))}`{(skippedSets.Count > 10 ? " …" : "")}");
        }

        lines.Add("- Run with `dotnet test`. SQL Server-only mappings (spatial types, SQL defaults, raw SQL) may need adjustments for SQLite.");

        foreach (var oldTests in context.Projects.Where(p => p.Layer == ProjectLayer.Tests))
        {
            context.Report.Todo(Name, $"Old Boilerplate test project {oldTests.Name} uses Boilerplate test infrastructure; move its tests to the generated " +
                                      $"{baseName}.*.Tests projects (derive from {prefix}ApplicationTestBase<TModule> / {prefix}EntityFrameworkCoreTestBase) and remove it.",
                context.Relative(oldTests.CsprojPath));
        }

        context.Log.WriteLine($"    generated 4 test projects ({dbSets.Count} DbSet, {appServices.Count} app service, {domainServices.Count} domain service tests)");
        context.RefreshProjects();
    }

    // ---------- discovery ----------

    private static ModuleInfo? FindModule(SolutionModel model, MigrationContext context, ProjectInfo project, Func<string, bool> nameFilter)
    {
        var modules = ClassesIn(model, context, project)
            .Where(c => SyntaxHelpers.HasBaseType(c.Syntax, "AbpModule"))
            .ToList();
        var module = modules.Where(c => nameFilter(c.Symbol.Name)).Select(c => c.Symbol).FirstOrDefault() ?? modules.Select(c => c.Symbol).FirstOrDefault();
        return module is null ? null : new ModuleInfo(module.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
    }

    private static INamedTypeSymbol? FindDbContext(SolutionModel model, MigrationContext context, ProjectInfo efCore) =>
        ClassesIn(model, context, efCore)
            .Where(c => SyntaxHelpers.HasBaseType(c.Syntax, "AbpDbContext") && !c.Symbol.IsAbstract)
            .Select(c => c.Symbol)
            .FirstOrDefault();

    private static List<string> FindDomainServices(SolutionModel model, MigrationContext context, ProjectInfo domain) =>
        ClassesIn(model, context, domain)
            .Where(c => !c.Symbol.IsAbstract && !c.Symbol.IsGenericType && !c.Symbol.IsStatic && InheritsDomainService(c.Symbol))
            .Select(c => c.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>DomainService directly or through solution base classes ({Prefix}DomainService, DomainServiceBase...).</summary>
    private static bool InheritsDomainService(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.Name == "DomainService") return true;
        }

        return false;
    }

    private static List<AppServiceInfo> FindAppServices(SolutionModel model, MigrationContext context)
    {
        var projects = context.Projects.Where(p => p.Layer is ProjectLayer.Application or ProjectLayer.ApplicationContracts).ToList();
        var implemented = projects.SelectMany(p => ClassesIn(model, context, p))
            .Where(c => !c.Symbol.IsAbstract)
            .SelectMany(c => c.Syntax.BaseList?.Types.Select(t => SyntaxHelpers.SimpleName(t.Type)) ?? [])
            .ToHashSet(StringComparer.Ordinal);

        var result = new List<AppServiceInfo>();
        foreach (var (tree, file) in model.FileOf)
        {
            if (!projects.Contains(context.FindProjectForFile(file)!)) continue;
            var semantic = model.Model(tree);
            foreach (var iface in tree.GetRoot().DescendantNodes().OfType<InterfaceDeclarationSyntax>())
            {
                if (iface.TypeParameterList is not null || !implemented.Contains(iface.Identifier.Text) || !IsAppServiceInterface(iface))
                {
                    continue;
                }

                var symbol = (INamedTypeSymbol)semantic.GetDeclaredSymbol(iface)!;
                result.Add(new AppServiceInfo(symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), GetListInput(iface, semantic)));
            }
        }

        return result.DistinctBy(a => a.Interface).OrderBy(a => a.Interface, StringComparer.Ordinal).ToList();
    }

    private static bool IsAppServiceInterface(InterfaceDeclarationSyntax iface) =>
        iface.BaseList?.Types.Any(t => SyntaxHelpers.SimpleName(t.Type) is var n &&
                                       (n is "IApplicationService" or "IRemoteService" || n.EndsWith("AppService", StringComparison.Ordinal))) == true;

    /// <summary>The TGetListInput of ICrudAppService/IReadOnlyAppService, when it can be created with "new".</summary>
    private static string? GetListInput(InterfaceDeclarationSyntax iface, SemanticModel semantic)
    {
        var crud = iface.BaseList!.Types.Select(t => t.Type).OfType<GenericNameSyntax>()
            .FirstOrDefault(g => g.Identifier.Text is "ICrudAppService" or "IReadOnlyAppService");
        if (crud is null)
        {
            return null;
        }

        var args = crud.TypeArgumentList.Arguments;
        TypeSyntax? input = crud.Identifier.Text == "ICrudAppService"
            ? args.Count switch { 2 => null, 3 or 4 or 5 => args[2], 6 => args[3], _ => null }
            : args.Count switch { 2 => null, 3 => args[2], 4 => args[3], _ => null };
        if (input is null)
        {
            return "global::Volo.Abp.Application.Dtos.PagedAndSortedResultRequestDto";
        }

        var name = SyntaxHelpers.SimpleName(input);
        if (name is "PagedAndSortedResultRequestDto" or "PagedResultRequestDto" or "LimitedResultRequestDto")
        {
            return $"global::Volo.Abp.Application.Dtos.{name}";
        }

        return semantic.GetTypeInfo(input).Type is INamedTypeSymbol { IsAbstract: false, TypeKind: TypeKind.Class } type &&
               type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public)
            ? type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : null;
    }

    private static (List<string> DbSets, List<string> Skipped) FindDbSets(SolutionModel model, MigrationContext context, ProjectInfo efCore, INamedTypeSymbol dbContext)
    {
        // Entities mapped to views / raw SQL / functions are not created by CreateTables().
        var efSource = string.Join("\n", FileSystem.EnumerateFiles(efCore.Directory, "*.cs").Where(f => !context.IsExcluded(f)).Select(File.ReadAllText));
        var nonTables = NonTableEntity().Matches(efSource).Select(m => m.Groups["type"].Value).ToHashSet(StringComparer.Ordinal);

        var sets = new List<string>();
        var skipped = new List<string>();
        foreach (var property in dbContext.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.Type is not INamedTypeSymbol { Name: "DbSet", TypeArguments.Length: 1 } dbSet)
            {
                // DbSet<T> is an EF Core type (unresolved in the model); fall back to syntax.
                var syntax = property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as PropertyDeclarationSyntax;
                if (syntax?.Type is not GenericNameSyntax { Identifier.Text: "DbSet" } generic) continue;
                var entity = SyntaxHelpers.SimpleName(generic.TypeArgumentList.Arguments[0]);
                (nonTables.Contains(entity) ? skipped : sets).Add(property.Name);
                continue;
            }

            (nonTables.Contains(dbSet.TypeArguments[0].Name) ? skipped : sets).Add(property.Name);
        }

        return (sets, skipped);
    }

    private static IEnumerable<(ClassDeclarationSyntax Syntax, INamedTypeSymbol Symbol)> ClassesIn(SolutionModel model, MigrationContext context, ProjectInfo project)
    {
        foreach (var (tree, file) in model.FileOf)
        {
            if (context.FindProjectForFile(file) != project) continue;
            var semantic = model.Model(tree);
            foreach (var cls in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (semantic.GetDeclaredSymbol(cls) is INamedTypeSymbol symbol)
                {
                    yield return (cls, symbol);
                }
            }
        }
    }

    // ---------- code templates ----------

    private static void Write(string directory, string relative, string content) =>
        FileSystem.WriteText(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)), content);

    private static string TestBaseModule(string ns, string prefix) => $$"""
        using Microsoft.Extensions.DependencyInjection;
        using Volo.Abp;
        using Volo.Abp.Authorization;
        using Volo.Abp.Autofac;
        using Volo.Abp.BackgroundJobs;
        using Volo.Abp.Data;
        using Volo.Abp.Modularity;
        using Volo.Abp.Testing;
        using Volo.Abp.Threading;

        namespace {{ns}};

        [DependsOn(
            typeof(AbpAutofacModule),
            typeof(AbpTestBaseModule),
            typeof(AbpAuthorizationModule),
            typeof(AbpBackgroundJobsAbstractionsModule))]
        public class {{prefix}}TestBaseModule : AbpModule
        {
            public override void ConfigureServices(ServiceConfigurationContext context)
            {
                Configure<AbpBackgroundJobOptions>(options =>
                {
                    options.IsJobExecutionEnabled = false;
                });

                context.Services.AddAlwaysAllowAuthorization();
            }

            public override void OnApplicationInitialization(ApplicationInitializationContext context)
            {
                SeedTestData(context);
            }

            private static void SeedTestData(ApplicationInitializationContext context)
            {
                AsyncHelper.RunSync(async () =>
                {
                    using (var scope = context.ServiceProvider.CreateScope())
                    {
                        await scope.ServiceProvider
                            .GetRequiredService<IDataSeeder>()
                            .SeedAsync();
                    }
                });
            }
        }

        """;

    private static string TestBaseClass(string ns, string prefix) => $$"""
        using System;
        using System.Threading.Tasks;
        using Microsoft.Extensions.DependencyInjection;
        using Volo.Abp;
        using Volo.Abp.Modularity;
        using Volo.Abp.Testing;
        using Volo.Abp.Uow;

        namespace {{ns}};

        /* All test classes are derived from this class, directly or indirectly. */
        public abstract class {{prefix}}TestBase<TStartupModule> : AbpIntegratedTest<TStartupModule>
            where TStartupModule : IAbpModule
        {
            protected override void SetAbpApplicationCreationOptions(AbpApplicationCreationOptions options)
            {
                options.UseAutofac();
            }

            protected virtual Task WithUnitOfWorkAsync(Func<Task> func)
            {
                return WithUnitOfWorkAsync(new AbpUnitOfWorkOptions(), func);
            }

            protected virtual async Task WithUnitOfWorkAsync(AbpUnitOfWorkOptions options, Func<Task> action)
            {
                using (var scope = ServiceProvider.CreateScope())
                {
                    var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();

                    using (var uow = uowManager.Begin(options))
                    {
                        await action();
                        await uow.CompleteAsync();
                    }
                }
            }

            protected virtual Task<TResult> WithUnitOfWorkAsync<TResult>(Func<Task<TResult>> func)
            {
                return WithUnitOfWorkAsync(new AbpUnitOfWorkOptions(), func);
            }

            protected virtual async Task<TResult> WithUnitOfWorkAsync<TResult>(AbpUnitOfWorkOptions options, Func<Task<TResult>> func)
            {
                using (var scope = ServiceProvider.CreateScope())
                {
                    var uowManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();

                    using (var uow = uowManager.Begin(options))
                    {
                        var result = await func();
                        await uow.CompleteAsync();
                        return result;
                    }
                }
            }
        }

        """;

    private static string SeedContributor(string ns, string prefix) => $$"""
        using System.Threading.Tasks;
        using Volo.Abp.Data;
        using Volo.Abp.DependencyInjection;

        namespace {{ns}};

        public class {{prefix}}TestDataSeedContributor : IDataSeedContributor, ITransientDependency
        {
            public Task SeedAsync(DataSeedContext context)
            {
                /* Seed additional test data here... */
                return Task.CompletedTask;
            }
        }

        """;

    private static string SimpleModule(string ns, string name, string dependency, string testDependency) => $$"""
        using Volo.Abp.Modularity;

        namespace {{ns}};

        [DependsOn(
            typeof({{dependency}}),
            typeof({{testDependency}}))]
        public class {{name}} : AbpModule
        {
        }

        """;

    private static string AbstractBase(string ns, string name, string baseClass) => $$"""
        using Volo.Abp.Modularity;

        namespace {{ns}};

        /* Inherit from this class for your tests of this layer. The tests are abstract and database independent;
         * the EntityFrameworkCore.Tests project runs them against SQLite. */
        public abstract class {{name}}<TStartupModule> : {{baseClass}}<TStartupModule>
            where TStartupModule : IAbpModule
        {
        }

        """;

    private static string DomainServiceTests(string ns, string prefix, List<string> services)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using Shouldly;");
        sb.AppendLine("using Volo.Abp.Modularity;");
        sb.AppendLine("using Xunit;");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns}.DomainServices;");
        sb.AppendLine();
        var used = new HashSet<string>(StringComparer.Ordinal);
        sb.AppendLine($"public abstract class {prefix}DomainServiceTests<TStartupModule> : {prefix}DomainTestBase<TStartupModule>");
        sb.AppendLine("    where TStartupModule : IAbpModule");
        sb.AppendLine("{");
        if (services.Count == 0)
        {
            sb.AppendLine("    // No DomainService classes were found. Add domain tests here.");
        }

        foreach (var service in services)
        {
            sb.AppendLine("    [Fact]");
            sb.AppendLine($"    public void {UniqueName(used, TestName(service))}_Should_Be_Resolvable()");
            sb.AppendLine("    {");
            sb.AppendLine($"        GetRequiredService<{service}>().ShouldNotBeNull();");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string AppServiceTests(string ns, string prefix, List<AppServiceInfo> services)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using Shouldly;");
        sb.AppendLine("using Volo.Abp.Modularity;");
        sb.AppendLine("using Xunit;");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns}.Services;");
        sb.AppendLine();
        var used = new HashSet<string>(StringComparer.Ordinal);
        sb.AppendLine($"public abstract class {prefix}AppServiceTests<TStartupModule> : {prefix}ApplicationTestBase<TStartupModule>");
        sb.AppendLine("    where TStartupModule : IAbpModule");
        sb.AppendLine("{");
        if (services.Count == 0)
        {
            sb.AppendLine("    // No application service interfaces were found. Add application tests here.");
        }

        foreach (var service in services)
        {
            var name = UniqueName(used, TestName(service.Interface));
            sb.AppendLine("    [Fact]");
            sb.AppendLine($"    public void {name}_Should_Be_Resolvable()");
            sb.AppendLine("    {");
            sb.AppendLine($"        GetRequiredService<{service.Interface}>().ShouldNotBeNull();");
            sb.AppendLine("    }");
            sb.AppendLine();
            if (service.GetListInput is not null)
            {
                sb.AppendLine("    [Fact]");
                sb.AppendLine($"    public async Task {name}_GetListAsync_Should_Return_A_Result()");
                sb.AppendLine("    {");
                sb.AppendLine($"        var result = await GetRequiredService<{service.Interface}>().GetListAsync(new {service.GetListInput}());");
                sb.AppendLine("        result.ShouldNotBeNull();");
                sb.AppendLine("    }");
                sb.AppendLine();
            }
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string EfTestModule(string ns, string prefix, string efModule, string dbContext, bool identity, bool spatial = false) => $$"""
        using Microsoft.Data.Sqlite;
        using Microsoft.EntityFrameworkCore;
        using Microsoft.EntityFrameworkCore.Infrastructure;
        using Microsoft.EntityFrameworkCore.Storage;
        using Microsoft.Extensions.DependencyInjection;
        using Volo.Abp;
        using Volo.Abp.EntityFrameworkCore;
        using Volo.Abp.EntityFrameworkCore.Sqlite;
        using Volo.Abp.Modularity;
        using Volo.Abp.Uow;
        {{(identity ? "using Volo.Abp.FeatureManagement;" + Environment.NewLine + "using Volo.Abp.PermissionManagement;" + Environment.NewLine + "using Volo.Abp.SettingManagement;" + Environment.NewLine : "")}}
        namespace {{ns}}.EntityFrameworkCore;

        [DependsOn(
            typeof({{efModule}}),
            typeof({{prefix}}ApplicationTestModule),
            typeof(AbpEntityFrameworkCoreSqliteModule))]
        public class {{prefix}}EntityFrameworkCoreTestModule : AbpModule
        {
            private SqliteConnection _sqliteConnection;

            public override void ConfigureServices(ServiceConfigurationContext context)
            {
        {{(identity ? """
                /* The definition stores of the ABP.io modules are static in tests (no database writes in the background). */
                Configure<FeatureManagementOptions>(options =>
                {
                    options.SaveStaticFeaturesToDatabase = false;
                    options.IsDynamicFeatureStoreEnabled = false;
                });
                Configure<PermissionManagementOptions>(options =>
                {
                    options.SaveStaticPermissionsToDatabase = false;
                    options.IsDynamicPermissionStoreEnabled = false;
                });
                Configure<SettingManagementOptions>(options =>
                {
                    options.SaveStaticSettingsToDatabase = false;
                    options.IsDynamicSettingStoreEnabled = false;
                });

        """ : "")}}        context.Services.AddAlwaysDisableUnitOfWorkTransaction();

                _sqliteConnection = CreateDatabaseAndGetConnection();
                Configure<AbpDbContextOptions>(options =>
                {
                    options.Configure(configurationContext =>
                    {
                        configurationContext.DbContextOptions.UseSqlite(_sqliteConnection{{(spatial ? ", sqlite => sqlite.UseNetTopologySuite()" : "")}});
                        configurationContext.DbContextOptions.ReplaceService<IModelCustomizer, SqliteTestModelCustomizer>();
                    });

                    /* The application module may configure this DbContext specifically (Boilerplate DbContextConfigurer): override it. */
                    options.Configure<{{dbContext}}>(configurationContext =>
                    {
                        configurationContext.DbContextOptions.UseSqlite(_sqliteConnection{{(spatial ? ", sqlite => sqlite.UseNetTopologySuite()" : "")}});
                        configurationContext.DbContextOptions.ReplaceService<IModelCustomizer, SqliteTestModelCustomizer>();
                    });
                });
            }

            public override void OnApplicationShutdown(ApplicationShutdownContext context)
            {
                _sqliteConnection?.Dispose();
            }

            private static SqliteConnection CreateDatabaseAndGetConnection()
            {
                var connection = new SqliteConnection("Data Source=:memory:");
                connection.Open();

                var options = new DbContextOptionsBuilder<{{dbContext}}>()
                    .UseSqlite(connection{{(spatial ? ", sqlite => sqlite.UseNetTopologySuite()" : "")}})
                    .ReplaceService<IModelCustomizer, SqliteTestModelCustomizer>()
                    .Options;

                using (var context = new {{dbContext}}(options))
                {
                    context.GetService<IRelationalDatabaseCreator>().CreateTables();
                }

                return connection;
            }
        }

        """;

    /// <summary>
    /// The model of a real solution often contains SQL Server SQL (computed columns, default value SQL, filtered indexes,
    /// check constraints) that SQLite cannot run. For the in-memory test database only, that SQL is removed from the model.
    /// </summary>
    private static string SqliteModelCustomizer(string ns) => $$"""
        using System.Linq;
        using Microsoft.EntityFrameworkCore;
        using Microsoft.EntityFrameworkCore.Infrastructure;
        using Microsoft.EntityFrameworkCore.Metadata;

        namespace {{ns}}.EntityFrameworkCore;

        /// <summary>
        /// Generated by AbpToVolo: removes provider-specific SQL (computed columns, default value SQL, index filters,
        /// check constraints) from the model of the SQLite test database. The production model is not affected.
        /// </summary>
        public class SqliteTestModelCustomizer : RelationalModelCustomizer
        {
            public SqliteTestModelCustomizer(ModelCustomizerDependencies dependencies)
                : base(dependencies)
            {
            }

            public override void Customize(ModelBuilder modelBuilder, DbContext context)
            {
                base.Customize(modelBuilder, context);

                foreach (var entityType in modelBuilder.Model.GetEntityTypes())
                {
                    foreach (var property in entityType.GetProperties())
                    {
                        if (property.GetComputedColumnSql() is not null)
                        {
                            property.SetComputedColumnSql(null);
                            property.ValueGenerated = ValueGenerated.Never;
                        }

                        if (property.GetDefaultValueSql() is not null)
                        {
                            property.SetDefaultValueSql(null);
                        }
                    }

                    foreach (var index in entityType.GetIndexes())
                    {
                        index.SetFilter(null);
                    }

                    foreach (var checkConstraint in entityType.GetCheckConstraints().ToList())
                    {
                        entityType.RemoveCheckConstraint(checkConstraint.ModelName);
                    }
                }
            }
        }

        """;

    private static string EfTestBase(string ns, string prefix) => $$"""
        namespace {{ns}}.EntityFrameworkCore;

        public abstract class {{prefix}}EntityFrameworkCoreTestBase : {{prefix}}TestBase<{{prefix}}EntityFrameworkCoreTestModule>
        {
        }

        """;

    /// <summary>ABP.io template: the EF Core tests share one collection, so they run one after another.</summary>
    private static string EfFixture(string ns, string prefix) => $$"""
        using System;
        using Xunit;

        namespace {{ns}}.EntityFrameworkCore;

        public class {{prefix}}EntityFrameworkCoreFixture : IDisposable
        {
            public void Dispose()
            {
            }
        }

        [CollectionDefinition({{prefix}}TestConsts.CollectionDefinitionName)]
        public class {{prefix}}EntityFrameworkCoreCollection : ICollectionFixture<{{prefix}}EntityFrameworkCoreFixture>
        {
        }

        """;

    private static string DbContextTests(string ns, string prefix, string dbContext, List<string> dbSets)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using Microsoft.EntityFrameworkCore;");
        sb.AppendLine("using Shouldly;");
        sb.AppendLine("using Volo.Abp.EntityFrameworkCore;");
        sb.AppendLine("using Xunit;");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns}.EntityFrameworkCore;");
        sb.AppendLine();
        sb.AppendLine("/* Every DbSet must be queryable: checks the EF Core mapping of each entity against a real (SQLite) database. */");
        sb.AppendLine($"[Collection({prefix}TestConsts.CollectionDefinitionName)]");
        sb.AppendLine($"public class {prefix}DbContextTests : {prefix}EntityFrameworkCoreTestBase");
        sb.AppendLine("{");
        foreach (var set in dbSets)
        {
            sb.AppendLine("    [Fact]");
            sb.AppendLine($"    public async Task {set}_Should_Be_Queryable()");
            sb.AppendLine("    {");
            sb.AppendLine("        await WithUnitOfWorkAsync(async () =>");
            sb.AppendLine("        {");
            sb.AppendLine($"            var dbContext = await GetRequiredService<IDbContextProvider<{dbContext}>>().GetDbContextAsync();");
            sb.AppendLine($"            (await dbContext.{set}.CountAsync()).ShouldBeGreaterThanOrEqualTo(0);");
            sb.AppendLine("        });");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string ConcreteTests(string ns, string name, string abstractName, string prefix, string abstractNamespace) => $$"""
        using {{abstractNamespace}};
        using Xunit;

        namespace {{ns}}.EntityFrameworkCore;

        [Collection({{prefix}}TestConsts.CollectionDefinitionName)]
        public class {{name}} : {{abstractName}}<{{prefix}}EntityFrameworkCoreTestModule>
        {
        }

        """;

    private static string TestConsts(string ns, string prefix) => $$"""
        namespace {{ns}};

        public static class {{prefix}}TestConsts
        {
            public const string CollectionDefinitionName = "{{prefix}} collection";
        }

        """;

    /// <summary>ABP.io template: tests run as the admin user (CurrentUser is set).</summary>
    private static string FakeCurrentPrincipalAccessor(string ns) => $$"""
        using System.Collections.Generic;
        using System.Security.Claims;
        using Volo.Abp.DependencyInjection;
        using Volo.Abp.Security.Claims;

        namespace {{ns}}.Security;

        [Dependency(ReplaceServices = true)]
        public class FakeCurrentPrincipalAccessor : ThreadCurrentPrincipalAccessor
        {
            private ClaimsPrincipal _principal;

            protected override ClaimsPrincipal GetClaimsPrincipal()
            {
                return GetPrincipal();
            }

            private ClaimsPrincipal GetPrincipal()
            {
                if (_principal == null)
                {
                    lock (this)
                    {
                        _principal ??= new ClaimsPrincipal(
                            new ClaimsIdentity(
                                new List<Claim>
                                {
                                    new Claim(AbpClaimTypes.UserId, "2e701e62-0953-4dd3-910b-dc6cc93ccb0d"),
                                    new Claim(AbpClaimTypes.UserName, "admin"),
                                    new Claim(AbpClaimTypes.Email, "admin@abp.io")
                                }
                            )
                        );
                    }
                }

                return _principal;
            }
        }

        """;

    /// <summary>ABP.io template sample: domain test with the Identity module's user manager and repository.</summary>
    private static string SampleDomainTests(string ns, string prefix) => $$"""
        using System.Threading.Tasks;
        using Shouldly;
        using Volo.Abp.Identity;
        using Volo.Abp.Modularity;
        using Xunit;

        namespace {{ns}}.Samples;

        /* This is just an example test class (from the ABP.io template). Write your domain tests like this. */
        public abstract class SampleDomainTests<TStartupModule> : {{prefix}}DomainTestBase<TStartupModule>
            where TStartupModule : IAbpModule
        {
            private readonly IIdentityUserRepository _identityUserRepository;
            private readonly IdentityUserManager _identityUserManager;

            protected SampleDomainTests()
            {
                _identityUserRepository = GetRequiredService<IIdentityUserRepository>();
                _identityUserManager = GetRequiredService<IdentityUserManager>();
            }

            [Fact]
            public async Task Should_Set_Email_Of_A_User()
            {
                IdentityUser adminUser;

                await WithUnitOfWorkAsync(async () =>
                {
                    adminUser = await _identityUserRepository.FindByNormalizedUserNameAsync("ADMIN");
                    await _identityUserManager.SetEmailAsync(adminUser, "newemail@abp.io");
                    await _identityUserRepository.UpdateAsync(adminUser);
                });

                adminUser = await _identityUserRepository.FindByNormalizedUserNameAsync("ADMIN");
                adminUser.Email.ShouldBe("newemail@abp.io");
            }
        }

        """;

    /// <summary>ABP.io template sample: application test with the Identity module's app service.</summary>
    private static string SampleAppServiceTests(string ns, string prefix) => $$"""
        using System.Threading.Tasks;
        using Shouldly;
        using Volo.Abp.Identity;
        using Volo.Abp.Modularity;
        using Xunit;

        namespace {{ns}}.Samples;

        /* This is just an example test class (from the ABP.io template). Write your application tests like this. */
        public abstract class SampleAppServiceTests<TStartupModule> : {{prefix}}ApplicationTestBase<TStartupModule>
            where TStartupModule : IAbpModule
        {
            private readonly IIdentityUserAppService _userAppService;

            protected SampleAppServiceTests()
            {
                _userAppService = GetRequiredService<IIdentityUserAppService>();
            }

            [Fact]
            public async Task Initial_Data_Should_Contain_Admin_User()
            {
                var result = await _userAppService.GetListAsync(new GetIdentityUsersInput());

                result.TotalCount.ShouldBeGreaterThan(0);
                result.Items.ShouldContain(u => u.UserName == "admin");
            }
        }

        """;

    /// <summary>ABP.io template sample: repository test against the real (SQLite) database.</summary>
    private static string SampleRepositoryTests(string ns, string prefix) => $$"""
        using System;
        using System.Linq;
        using System.Threading.Tasks;
        using Microsoft.EntityFrameworkCore;
        using Shouldly;
        using Volo.Abp.Domain.Repositories;
        using Volo.Abp.Identity;
        using Xunit;

        namespace {{ns}}.EntityFrameworkCore.Samples;

        /* This is just an example test class (from the ABP.io template). Write your EF Core repository tests like this. */
        [Collection({{prefix}}TestConsts.CollectionDefinitionName)]
        public class SampleRepositoryTests : {{prefix}}EntityFrameworkCoreTestBase
        {
            private readonly IRepository<IdentityUser, Guid> _appUserRepository;

            public SampleRepositoryTests()
            {
                _appUserRepository = GetRequiredService<IRepository<IdentityUser, Guid>>();
            }

            [Fact]
            public async Task Should_Query_AppUser()
            {
                await WithUnitOfWorkAsync(async () =>
                {
                    var adminUser = await (await _appUserRepository.GetQueryableAsync())
                        .Where(u => u.UserName == "admin")
                        .FirstOrDefaultAsync();

                    adminUser.ShouldNotBeNull();
                });
            }
        }

        """;

    private static string UniqueName(HashSet<string> used, string name)
    {
        var candidate = name;
        for (var i = 2; !used.Add(candidate); i++) candidate = name + i;
        return candidate;
    }

    private static string TestName(string fullName)
    {
        var name = fullName.Replace("global::", "");
        return new string(name.Split('.').Last().Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
    }

    [GeneratedRegex(@"Entity<(?<type>\w+)>\(\)[\s\S]{0,400}?\.(ToView|ToSqlQuery|ToFunction|HasNoKey)\(")]
    private static partial Regex NonTableEntity();
}
