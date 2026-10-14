using System.Xml.Linq;
using AbpToVolo.Core;
using AbpToVolo.Steps;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AbpToVolo.Tests;

/// <summary>Runs the whole engine on samples/Acme.PhoneBook (offline: no nuget.org lookup, no build).</summary>
public sealed class EndToEndTests : IDisposable
{
    private readonly string _output = Path.Combine(Path.GetTempPath(), "abptovolo-tests", Guid.NewGuid().ToString("N"));

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

    private MigrationReport Run(bool renameLayers = false, bool contracts = true, bool tests = true, MigrationsMode migrations = MigrationsMode.Squash)
    {
        var steps = MigrationEngine.DefaultSteps()
            .Select(s => s is ResolveVersionsStep ? new ResolveVersionsStep(_ => null) : s)
            .Where(s => s is not (BuildVerificationStep or BuildFixStep or AbpIoModulesMigrationStep));
        var options = new MigrationOptions
        {
            InputPath = SamplePath,
            OutputPath = _output,
            RenameLayers = renameLayers,
            CreateContracts = contracts,
            CreateTests = tests,
            Migrations = migrations
        };
        return new MigrationEngine(steps).Run(options, TextWriter.Null);
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(_output, relative));

    private static string Lf(string s) => s.Replace("\r\n", "\n");

    [Fact]
    public void Converts_the_sample_solution()
    {
        var report = Run();

        Assert.Equal(12, report.ProjectsMigrated); // 5 converted + Domain.Shared + Application.Contracts + DbMigrator + 4 test projects
        Assert.True(report.CSharpFilesChanged > 15);
        Assert.True(File.Exists(Path.Combine(_output, ReportWriterStep.MarkdownFile)));
        Assert.True(File.Exists(Path.Combine(_output, ReportWriterStep.JsonFile)));
        Assert.True(Directory.Exists(SamplePath), "input must not be touched");
    }

    [Fact]
    public void No_boilerplate_usings_or_packages_remain_and_all_code_parses()
    {
        Run();

        foreach (var file in Directory.EnumerateFiles(_output, "*.cs", SearchOption.AllDirectories))
        {
            var code = File.ReadAllText(file);
            Assert.DoesNotContain("using Abp.", code);
            Assert.DoesNotContain("using Castle.", code);
            var errors = CSharpSyntaxTree.ParseText(code).GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);
            Assert.Empty(errors);
        }

        foreach (var csproj in Directory.EnumerateFiles(_output, "*.csproj", SearchOption.AllDirectories))
        {
            var doc = XDocument.Load(csproj);
            Assert.Equal("net10.0", doc.Descendants("TargetFramework").Single().Value);
            var packages = doc.Descendants("PackageReference").Select(p => p.Attribute("Include")!.Value).ToList();
            Assert.DoesNotContain(packages, p => p == "Abp" || p.StartsWith("Abp.") || p.StartsWith("Castle."));
        }
    }

    [Fact]
    public void Project_files_get_layer_packages_and_offline_ms_version()
    {
        Run();

        var core = Read("src/Acme.PhoneBook.Core/Acme.PhoneBook.Core.csproj");
        Assert.Contains("<PackageReference Include=\"Volo.Abp.Ddd.Domain\" Version=\"10.7.0\" />", core);
        Assert.Contains("..\\Acme.PhoneBook.Domain.Shared\\Acme.PhoneBook.Domain.Shared.csproj", core);

        var shared = Read("src/Acme.PhoneBook.Domain.Shared/Acme.PhoneBook.Domain.Shared.csproj");
        Assert.Contains("<PackageReference Include=\"Volo.Abp.Ddd.Domain.Shared\" Version=\"10.7.0\" />", shared);
        Assert.Contains("<GenerateEmbeddedFilesManifest>true</GenerateEmbeddedFilesManifest>", shared);
        Assert.Contains("Localization\\PhoneBook\\*.json", shared);

        var ef = Read("src/Acme.PhoneBook.EntityFrameworkCore/Acme.PhoneBook.EntityFrameworkCore.csproj");
        Assert.Contains("Volo.Abp.EntityFrameworkCore.SqlServer", ef);
        Assert.Contains("<PackageReference Include=\"Microsoft.EntityFrameworkCore.SqlServer\" Version=\"10.0.*\" />", ef);

        Assert.Contains("Volo.Abp.Ddd.Application", Read("src/Acme.PhoneBook.Application/Acme.PhoneBook.Application.csproj"));
        Assert.Contains("Volo.Abp.Ddd.Application.Contracts", Read("src/Acme.PhoneBook.Application.Contracts/Acme.PhoneBook.Application.Contracts.csproj"));
        Assert.Contains("Volo.Abp.Autofac", Read("src/Acme.PhoneBook.Web.Host/Acme.PhoneBook.Web.Host.csproj"));
    }

    [Fact]
    public void Localization_is_converted_to_json_in_domain_shared()
    {
        Run();

        var fa = Read("src/Acme.PhoneBook.Domain.Shared/Localization/PhoneBook/fa.json");
        Assert.Contains("\"culture\": \"fa\"", fa);
        Assert.Contains("\"People\": \"اشخاص\"", fa);
        Assert.Contains("[LocalizationResourceName(\"PhoneBook\")]", Read("src/Acme.PhoneBook.Domain.Shared/Localization/PhoneBookResource.cs"));
        Assert.Contains("AddVirtualJson(\"/Localization/PhoneBook\")", Read("src/Acme.PhoneBook.Domain.Shared/PhoneBookDomainSharedModule.cs"));
        Assert.DoesNotContain("AddVirtualJson", Read("src/Acme.PhoneBook.Core/PhoneBookCoreModule.cs"));
        Assert.False(File.Exists(Path.Combine(_output, "src/Acme.PhoneBook.Core/Localization/PhoneBookLocalizationConfigurer.cs")));
        Assert.Contains("LocalizationResource = typeof(PhoneBookResource);", Read("src/Acme.PhoneBook.Application/PhoneBookAppServiceBase.cs"));
    }

    [Fact]
    public void Migrations_are_squashed_keeping_the_last_id_and_the_original_operations()
    {
        Run();

        var folder = Path.Combine(_output, "src/Acme.PhoneBook.EntityFrameworkCore/Migrations");
        var files = Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).ToList();
        // the original migrations are gone; only the generated ones (ids right after the last one) have an id prefix
        Assert.DoesNotContain(files, f => (f.StartsWith("2024", StringComparison.Ordinal) || f.StartsWith("2025", StringComparison.Ordinal)) &&
                                          !f.Contains(ZeroDatabase.RenameMigrationName, StringComparison.Ordinal) &&
                                          !f.Contains(ZeroForeignKeys.MigrationName, StringComparison.Ordinal));
        Assert.Contains($"{EfMigrationsStep.SquashedClassName}.2024.cs", files);
        Assert.Contains($"{EfMigrationsStep.SquashedClassName}.2025.cs", files);
        Assert.Contains(EfMigrationsStep.HistoryFile, files);
        Assert.Contains(EfMigrationsStep.PrecheckFile, files);

        var designer = File.ReadAllText(Path.Combine(folder, $"{EfMigrationsStep.SquashedClassName}.Designer.cs"));
        Assert.Contains("[Migration(\"20250102000000_AddPersonView\")]", designer);

        var year2024 = File.ReadAllText(Path.Combine(folder, $"{EfMigrationsStep.SquashedClassName}.2024.cs"));
        Assert.True(year2024.IndexOf("20240101000000_Initial", StringComparison.Ordinal) < year2024.IndexOf("20240315000000_AddPhones", StringComparison.Ordinal));
        Assert.Contains("CreatorUserId = table.Column<long>", year2024); // migrations are never rewritten
        Assert.Contains("نمای اشخاص", File.ReadAllText(Path.Combine(folder, $"{EfMigrationsStep.SquashedClassName}.2025.cs")));

        var main = File.ReadAllText(Path.Combine(folder, $"{EfMigrationsStep.SquashedClassName}.cs"));
        Assert.True(main.IndexOf("Up2024", StringComparison.Ordinal) < main.IndexOf("Up2025", StringComparison.Ordinal));
        Assert.True(main.IndexOf("Down2025", StringComparison.Ordinal) < main.IndexOf("Down2024", StringComparison.Ordinal));

        Assert.Contains("20240315000000_AddPhones", File.ReadAllText(Path.Combine(folder, EfMigrationsStep.HistoryFile)));
        Assert.Contains("N'20250102000000_AddPersonView'", File.ReadAllText(Path.Combine(folder, EfMigrationsStep.PrecheckFile)));
        Assert.True(File.Exists(Path.Combine(_output, "src/Acme.PhoneBook.Core", CompatShimStep.ShimFolder, "ObjectMapperCompatExtensions.cs")));
    }

    [Fact]
    public void Contracts_receive_shared_types_dtos_and_interfaces()
    {
        Run();

        var shared = Path.Combine(_output, "src/Acme.PhoneBook.Domain.Shared");
        var contracts = Path.Combine(_output, "src/Acme.PhoneBook.Application.Contracts");

        // Domain.Shared: consts, enums (extracted from the entity file, doc comment intact), entity consts
        Assert.True(File.Exists(Path.Combine(shared, "PhoneBookConsts.cs")));
        Assert.True(File.Exists(Path.Combine(shared, "Authorization/PermissionNames.cs")));
        var phoneType = Lf(File.ReadAllText(Path.Combine(shared, "People/PhoneType.cs")));
        Assert.Contains("/// <summary>\n/// نوع تلفن (multi-line doc comment)\n/// </summary>\npublic enum PhoneType : byte", phoneType);
        Assert.DoesNotContain("enum PhoneType", Read("src/Acme.PhoneBook.Core/People/Phone.cs"));
        Assert.Contains("public const int MaxNameLength = 32;", File.ReadAllText(Path.Combine(shared, "People/PersonConsts.cs")));
        Assert.Contains("MaxNameLength = PersonConsts.MaxNameLength;", Read("src/Acme.PhoneBook.Core/People/Person.cs"));

        // Application.Contracts: DTOs, interfaces, permission provider; [AutoMap] became a Profile in Application
        var dtos = File.ReadAllText(Path.Combine(contracts, "People/Dto/PeopleDtos.cs"));
        Assert.Contains("[StringLength(PersonConsts.MaxNameLength)]", dtos);
        Assert.DoesNotContain("AutoMap", dtos);
        Assert.True(File.Exists(Path.Combine(contracts, "People/IPersonAppService.cs")));
        Assert.True(File.Exists(Path.Combine(contracts, "People/IPhoneAppService.cs")));
        Assert.True(File.Exists(Path.Combine(contracts, "Authorization/PhoneBookAuthorizationProvider.cs")));
        Assert.DoesNotContain("interface IPhoneAppService", Read("src/Acme.PhoneBook.Application/People/PhoneAppService.cs"));
        var profile = Read("src/Acme.PhoneBook.Application/PhoneBookApplicationAutoMapperProfile.cs");
        Assert.Contains("CreateMap<global::Acme.PhoneBook.People.Person, global::Acme.PhoneBook.People.Dto.PersonDto>();", profile);
        Assert.Contains("CreateMap<global::Acme.PhoneBook.People.Person, global::Acme.PhoneBook.People.Dto.CreatePersonDto>().ReverseMap();", profile);

        // modules and solution wired
        Assert.Contains("class PhoneBookDomainSharedModule", Read("src/Acme.PhoneBook.Domain.Shared/PhoneBookDomainSharedModule.cs"));
        Assert.Contains("typeof(PhoneBookDomainSharedModule)", Read("src/Acme.PhoneBook.Application.Contracts/PhoneBookApplicationContractsModule.cs"));
        Assert.Contains("typeof(PhoneBookDomainSharedModule)", Read("src/Acme.PhoneBook.Core/PhoneBookCoreModule.cs"));
        Assert.Contains("typeof(PhoneBookApplicationContractsModule)", Read("src/Acme.PhoneBook.Application/PhoneBookApplicationModule.cs"));
        Assert.Contains("Acme.PhoneBook.Application.Contracts.csproj", Read("Acme.PhoneBook.sln"));
    }

    [Fact]
    public void Abp_style_test_projects_are_generated()
    {
        Run();

        Assert.Contains("class PhoneBookTestBaseModule", Read("test/Acme.PhoneBook.TestBase/PhoneBookTestBaseModule.cs"));
        Assert.Contains("AbpIntegratedTest<TStartupModule>", Read("test/Acme.PhoneBook.TestBase/PhoneBookTestBase.cs"));
        Assert.Contains("GetRequiredService<global::Acme.PhoneBook.People.PersonManager>()",
            Read("test/Acme.PhoneBook.Domain.Tests/DomainServices/PhoneBookDomainServiceTests.cs"));

        var appTests = Read("test/Acme.PhoneBook.Application.Tests/Services/PhoneBookAppServiceTests.cs");
        Assert.Contains("public abstract class PhoneBookAppServiceTests<TStartupModule>", appTests);
        Assert.Contains("GetListAsync(new global::Acme.PhoneBook.People.Dto.GetPeopleInput())", appTests);

        const string ef = "test/Acme.PhoneBook.EntityFrameworkCore.Tests/EntityFrameworkCore";
        Assert.Contains("UseSqlite(", Read($"{ef}/PhoneBookEntityFrameworkCoreTestModule.cs"));
        var dbTests = Read($"{ef}/PhoneBookDbContextTests.cs");
        Assert.Contains("People_Should_Be_Queryable", dbTests);
        Assert.Contains("Phones_Should_Be_Queryable", dbTests);
        Assert.Contains(": PhoneBookAppServiceTests<PhoneBookEntityFrameworkCoreTestModule>", Read($"{ef}/Applications/EfCorePhoneBookAppServiceTests.cs"));
        Assert.Contains(": PhoneBookDomainServiceTests<PhoneBookEntityFrameworkCoreTestModule>", Read($"{ef}/Domains/EfCorePhoneBookDomainServiceTests.cs"));
        Assert.Contains("Acme.PhoneBook.EntityFrameworkCore.Tests.csproj", Read("Acme.PhoneBook.sln"));
    }

    [Fact]
    public void Features_can_be_switched_off()
    {
        var report = Run(contracts: false, tests: false, migrations: MigrationsMode.Delete);

        Assert.Equal(6, report.ProjectsMigrated); // 5 converted + the ABP.io DbMigrator
        Assert.False(Directory.Exists(Path.Combine(_output, "src/Acme.PhoneBook.Domain.Shared")));
        Assert.False(Directory.Exists(Path.Combine(_output, "test")));
        Assert.False(Directory.Exists(Path.Combine(_output, "src/Acme.PhoneBook.EntityFrameworkCore/Migrations")));
        Assert.True(File.Exists(Path.Combine(_output, "src/Acme.PhoneBook.Core/Localization/PhoneBook/fa.json")));
        Assert.Contains("AddVirtualJson", Read("src/Acme.PhoneBook.Core/PhoneBookCoreModule.cs"));
    }

    [Fact]
    public void Rename_layers_moves_projects_and_fixes_references()
    {
        Run(renameLayers: true);

        Assert.True(File.Exists(Path.Combine(_output, "src/Acme.PhoneBook.Domain/Acme.PhoneBook.Domain.csproj")));
        Assert.True(File.Exists(Path.Combine(_output, "src/Acme.PhoneBook.HttpApi/Acme.PhoneBook.HttpApi.csproj")));
        Assert.True(File.Exists(Path.Combine(_output, "src/Acme.PhoneBook.HttpApi.Host/Acme.PhoneBook.HttpApi.Host.csproj")));
        Assert.True(File.Exists(Path.Combine(_output, "src/Acme.PhoneBook.Domain.Shared/Acme.PhoneBook.Domain.Shared.csproj")));

        var sln = Read("Acme.PhoneBook.sln");
        Assert.Contains("src\\Acme.PhoneBook.Domain\\Acme.PhoneBook.Domain.csproj", sln);
        Assert.Contains("src\\Acme.PhoneBook.HttpApi.Host\\Acme.PhoneBook.HttpApi.Host.csproj", sln);
        Assert.DoesNotContain("Acme.PhoneBook.Core", sln);
        Assert.Contains("..\\Acme.PhoneBook.Domain\\Acme.PhoneBook.Domain.csproj", Read("src/Acme.PhoneBook.Application/Acme.PhoneBook.Application.csproj"));
    }

    [Fact]
    public void Refuses_to_overwrite_a_non_empty_output_without_force()
    {
        Directory.CreateDirectory(_output);
        File.WriteAllText(Path.Combine(_output, "keep.txt"), "x");

        Assert.Throws<MigrationException>(() => Run());
        Assert.True(File.Exists(Path.Combine(_output, "keep.txt")));
    }

    [Fact]
    public void Endpoints_stay_in_the_build_and_keep_the_boilerplate_api()
    {
        Run(renameLayers: true);

        // Template endpoints (users, token auth) are not moved to the legacy folder: their routes must stay.
        Assert.True(File.Exists(Path.Combine(_output, "src/Acme.PhoneBook.Application/Users/UserAppService.cs")));
        Assert.True(File.Exists(Path.Combine(_output, "src/Acme.PhoneBook.HttpApi/Controllers/TokenAuthController.cs")));

        var api = Read($"src/Acme.PhoneBook.HttpApi/{CompatShimStep.ShimFolder}/BoilerplateApi.cs");
        Assert.Contains("class BoilerplateConventionalRouteBuilder : ConventionalRouteBuilder", api);
        Assert.Contains("$\"api/services/{rootPath}/{BoilerplateApi.ServiceName(type)}/{ActionName(action, type)}\"", api);
        Assert.Contains("? \"GetAll\"", api); // CRUD GetListAsync keeps the Boilerplate route
        Assert.Contains("class BoilerplateResultWrapFilter : IAsyncAlwaysRunResultFilter", api);
        Assert.Contains("global::Acme.PhoneBook.BoilerplateApi.ConfigureServices(context.Services);", Read("src/Acme.PhoneBook.HttpApi/PhoneBookHttpApiModule.cs"));
        var models = Read($"src/Acme.PhoneBook.Domain.Shared/{CompatShimStep.ShimFolder}/BoilerplateApiModels.cs");
        Assert.Contains("public class AjaxResponse : AjaxResponse<object>", models);
        Assert.Contains("[JsonPropertyName(\"__abp\")]", models);
    }

    public void Dispose()
    {
        if (Directory.Exists(_output)) Directory.Delete(_output, recursive: true);
    }
}
