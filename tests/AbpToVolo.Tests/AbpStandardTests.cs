using AbpToVolo.Core;
using AbpToVolo.Steps;

namespace AbpToVolo.Tests;

/// <summary>
/// The default options (ABP.io layer names, Module Zero → ABP.io modules, standard host, Volo tests) on samples/Acme.PhoneBook,
/// offline and without building.
/// </summary>
public sealed class AbpStandardTests : IDisposable
{
    private readonly string _output = Path.Combine(Path.GetTempPath(), "abptovolo-tests", Guid.NewGuid().ToString("N"));
    private readonly MigrationReport _report;

    public AbpStandardTests()
    {
        var steps = MigrationEngine.DefaultSteps()
            .Select(s => s is ResolveVersionsStep ? new ResolveVersionsStep(_ => null) : s)
            .Where(s => s is not (BuildVerificationStep or BuildFixStep));
        _report = new MigrationEngine(steps).Run(new MigrationOptions { InputPath = SamplePath, OutputPath = _output }, TextWriter.Null);
    }

    private static string SamplePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AbpToVolo.slnx")))
            {
                dir = dir.Parent;
            }

            return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Repository root not found"), "samples", "Acme.PhoneBook");
        }
    }

    private string Path_(string relative) => Path.Combine(_output, relative.Replace('/', Path.DirectorySeparatorChar));

    private string Read(string relative) => File.ReadAllText(Path_(relative));

    [Fact]
    public void Layers_and_modules_use_abp_io_names()
    {
        Assert.True(File.Exists(Path_("src/Acme.PhoneBook.Domain/Acme.PhoneBook.Domain.csproj")));
        Assert.False(Directory.Exists(Path_("src/Acme.PhoneBook.Core")));
        Assert.Contains("class PhoneBookDomainModule", Read("src/Acme.PhoneBook.Domain/PhoneBookDomainModule.cs"));
        Assert.Contains("class PhoneBookHttpApiModule", Read("src/Acme.PhoneBook.HttpApi/PhoneBookHttpApiModule.cs"));
        Assert.Contains("class PhoneBookHttpApiHostModule", Read("src/Acme.PhoneBook.HttpApi.Host/PhoneBookHttpApiHostModule.cs"));
        Assert.Contains("class PhoneBookDbMigratorModule", Read("src/Acme.PhoneBook.DbMigrator/PhoneBookDbMigratorModule.cs"));
    }

    [Fact]
    public void Module_zero_is_replaced_by_the_abp_io_modules()
    {
        // The Boilerplate User/Role/Tenant classes are kept for reference only (not compiled).
        Assert.True(File.Exists(Path_($"src/Acme.PhoneBook.Domain/{FileSystem.LegacyFolder}/Authorization/Users/User.cs")));
        Assert.False(File.Exists(Path_("src/Acme.PhoneBook.Domain/Authorization/Users/User.cs")));

        var dbContext = Read("src/Acme.PhoneBook.EntityFrameworkCore/EntityFrameworkCore/PhoneBookDbContext.cs");
        Assert.Contains("[ReplaceDbContext(typeof(IIdentityDbContext))]", dbContext);
        Assert.Contains("[ReplaceDbContext(typeof(ITenantManagementDbContext))]", dbContext);
        Assert.Contains("modelBuilder.ConfigureIdentity();", dbContext);

        // Custom User properties → module entity extensions
        Assert.True(File.Exists(Path_("src/Acme.PhoneBook.Domain/PhoneBookModuleExtensionConfigurator.cs")));
        Assert.True(File.Exists(Path_("src/Acme.PhoneBook.EntityFrameworkCore/EntityFrameworkCore/PhoneBookEfCoreEntityExtensionMappings.cs")));
    }

    [Fact]
    public void Module_zero_tables_are_renamed_and_the_data_migration_script_is_generated()
    {
        var migrations = Path_("src/Acme.PhoneBook.EntityFrameworkCore/Migrations");
        var rename = Directory.EnumerateFiles(migrations, "*_RenameModuleZeroTables.cs").Single();
        Assert.Contains("(N'AbpUsers', N'Zero_AbpUsers')", File.ReadAllText(rename));
        Assert.Single(Directory.EnumerateFiles(migrations, "*_RenameModuleZeroTables.Designer.cs"));
        Assert.Contains("Zero_IdMap", Read("src/Acme.PhoneBook.EntityFrameworkCore/ModuleZeroDataMigration.sql"));
    }

    [Fact]
    public void Service_base_classes_follow_the_template()
    {
        Assert.Contains("public abstract class PhoneBookDomainService : DomainService", Read("src/Acme.PhoneBook.Domain/PhoneBookDomainService.cs"));
        Assert.Contains("class PersonManager : PhoneBookDomainService", Read("src/Acme.PhoneBook.Domain/People/PersonManager.cs"));
        Assert.Contains("public abstract class PhoneBookAppService : ApplicationService", Read("src/Acme.PhoneBook.Application/PhoneBookAppService.cs"));
    }

    [Fact]
    public void Volo_default_tests_are_generated()
    {
        Assert.True(File.Exists(Path_("test/Acme.PhoneBook.Domain.Tests/Samples/SampleDomainTests.cs")));
        Assert.True(File.Exists(Path_("test/Acme.PhoneBook.Application.Tests/Samples/SampleAppServiceTests.cs")));
        Assert.True(File.Exists(Path_("test/Acme.PhoneBook.EntityFrameworkCore.Tests/EntityFrameworkCore/Samples/SampleRepositoryTests.cs")));
        Assert.True(File.Exists(Path_("test/Acme.PhoneBook.TestBase/Security/FakeCurrentPrincipalAccessor.cs")));
        Assert.Contains("[Collection(", Read("test/Acme.PhoneBook.EntityFrameworkCore.Tests/EntityFrameworkCore/Domains/EfCoreSampleDomainTests.cs"));
    }

    [Fact]
    public void Host_uses_minimal_hosting_and_openiddict()
    {
        var module = Read("src/Acme.PhoneBook.HttpApi.Host/PhoneBookHttpApiHostModule.cs");
        Assert.Contains("ForwardIdentityAuthenticationForBearer", module);
        Assert.Contains("app.UseConfiguredEndpoints()", module);
        Assert.Contains("AddApplicationAsync<PhoneBookHttpApiHostModule>", Read("src/Acme.PhoneBook.HttpApi.Host/Program.cs"));
        Assert.Contains("OpenIddictDataSeedContributor", Read("src/Acme.PhoneBook.Domain/OpenIddict/PhoneBookOpenIddictDataSeedContributor.cs"));
    }

    [Fact]
    public void Report_lists_the_steps()
    {
        Assert.Equal(12, _report.ProjectsMigrated); // 5 converted + Domain.Shared + Application.Contracts + DbMigrator + 4 test projects
        var markdown = Read(ReportWriterStep.MarkdownFile);
        Assert.Contains("Module Zero", markdown);
    }

    [Fact]
    public void Generated_migrations_have_unique_ids_and_convert_the_audit_columns()
    {
        var migrations = Path_("src/Acme.PhoneBook.EntityFrameworkCore/Migrations");
        var ids = Directory.GetFiles(migrations, "*.Designer.cs")
            .Select(f => System.Text.RegularExpressions.Regex.Match(File.ReadAllText(f), "\\[Migration\\(\"(\\d{14})_").Groups[1].Value)
            .ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        var keys = Directory.GetFiles(migrations, $"*_{ZeroForeignKeys.MigrationName}.cs").Single();
        Assert.Contains("N'CreatorUserId', N'CreatorId', N'User'", File.ReadAllText(keys));
        Assert.Contains("b.Property<Guid?>(\"CreatorId\")", Read("src/Acme.PhoneBook.EntityFrameworkCore/Migrations/PhoneBookDbContextModelSnapshot.cs"));
        Assert.True(File.Exists(Path_("test/Acme.PhoneBook.EntityFrameworkCore.Tests/EntityFrameworkCore/SqliteTestModelCustomizer.cs")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_output, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
