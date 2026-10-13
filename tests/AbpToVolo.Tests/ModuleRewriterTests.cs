using AbpToVolo.Core;

namespace AbpToVolo.Tests;

public class ModuleRewriterTests : RewriterTestBase
{
    private const string ZeroCoreModule = """
        using Abp.Modules;
        using Abp.Reflection.Extensions;
        using Abp.Zero;

        namespace Acme
        {
            [DependsOn(typeof(AbpZeroCoreModule))]
            public class AcmeCoreModule : AbpModule
            {
                public override void PreInitialize()
                {
                    Configuration.MultiTenancy.IsEnabled = true;
                    Configuration.Auditing.IsEnabled = false;
                    Configuration.BackgroundJobs.IsJobExecutionEnabled = false;
                    Configuration.Navigation.Providers.Add<AcmeNavigationProvider>();
                    Configuration.Modules.Zero().LanguageManagement.EnableDbLocalization();
                }

                public override void Initialize()
                {
                    IocManager.RegisterAssemblyByConvention(typeof(AcmeCoreModule).GetAssembly());
                    IocManager.Register<IClock, MyClock>(DependencyLifeStyle.Transient);
                    IocManager.Register<MyCache>();
                }

                public override void PostInitialize()
                {
                    IocManager.Resolve<AppTimes>().StartupTime = System.DateTime.Now;
                }

                public override void Shutdown()
                {
                }
            }
        }
        """;

    [Fact]
    public void Lifecycle_methods_are_renamed_with_context_parameters()
    {
        var (code, _) = Migrate(ZeroCoreModule);

        AssertContains("public override void PreConfigureServices(ServiceConfigurationContext context)", code);
        AssertContains("public override void ConfigureServices(ServiceConfigurationContext context)", code);
        AssertContains("public override void OnPostApplicationInitialization(ApplicationInitializationContext context)", code);
        AssertContains("public override void OnApplicationShutdown(ApplicationShutdownContext context)", code);
        AssertContains("using Volo.Abp.Modularity;", code);
    }

    [Fact]
    public void Configuration_assignments_become_options()
    {
        var (code, _) = Migrate(ZeroCoreModule);

        AssertContains("Configure<AbpMultiTenancyOptions>(options => { options.IsEnabled = true; });", code);
        AssertContains("Configure<AbpAuditingOptions>(options => { options.IsEnabled = false; });", code);
        AssertContains("Configure<AbpBackgroundJobOptions>(options => { options.IsJobExecutionEnabled = false; });", code);
        AssertContains("using Volo.Abp.MultiTenancy;", code);
    }

    [Fact]
    public void Unsupported_configuration_is_commented_out_with_todo()
    {
        var (code, _) = Migrate(ZeroCoreModule);

        AssertContains("// TODO [AbpToVolo]: Navigation providers become IMenuContributor", code);
        AssertContains("// Configuration.Navigation.Providers.Add<AcmeNavigationProvider>();", code);
        AssertContains("// Configuration.Modules.Zero().LanguageManagement.EnableDbLocalization();", code);
    }

    [Fact]
    public void IocManager_registrations_are_converted()
    {
        var (code, _) = Migrate(ZeroCoreModule);

        AssertNotContains("RegisterAssemblyByConvention(typeof", code);
        AssertContains("context.Services.AddTransient<IClock, MyClock>();", code);
        AssertContains("context.Services.AddSingleton<MyCache>();", code); // Boilerplate default lifestyle is Singleton
        AssertContains("context.ServiceProvider.GetRequiredService<AppTimes>().StartupTime", code);
    }

    [Fact]
    public void Zero_module_dependency_maps_to_abp_io_modules()
    {
        var (code, _) = Migrate(ZeroCoreModule, ProjectLayer.Domain);

        AssertContains("typeof(AbpDddDomainModule)", code);
        AssertContains("typeof(AbpIdentityDomainModule)", code);
        AssertContains("typeof(AbpPermissionManagementDomainIdentityModule)", code);
        AssertContains("typeof(AbpTenantManagementDomainModule)", code);
        AssertContains("using Volo.Abp.Identity;", code);
        AssertNotContains("AbpZeroCoreModule", code);
    }

    [Fact]
    public void Layer_modules_are_added_to_DependsOn()
    {
        const string source = """
            using Abp.AutoMapper;
            using Abp.Modules;

            [DependsOn(typeof(AcmeCoreModule), typeof(AbpAutoMapperModule))]
            public class AcmeApplicationModule : AbpModule
            {
                public override void Initialize()
                {
                    var thisAssembly = typeof(AcmeApplicationModule).GetAssembly();
                    Configuration.Modules.AbpAutoMapper().Configurators.Add(cfg => cfg.AddMaps(thisAssembly));
                }
            }
            """;

        var (code, _) = Migrate(source, ProjectLayer.Application);

        AssertContains("[DependsOn(typeof(AcmeCoreModule), typeof(AbpAutoMapperModule), typeof(AbpDddApplicationModule))]", code);
        AssertContains("Configure<AbpAutoMapperOptions>(options => { options.AddMaps<AcmeApplicationModule>(validate: false); });", code);
        AssertContains("var thisAssembly = typeof(AcmeApplicationModule).Assembly;", code);
    }

    [Fact]
    public void Ef_core_and_controller_registration_is_converted()
    {
        const string source = """
            using Abp.Modules;

            public class AcmeWebCoreModule : AbpModule
            {
                public override void PreInitialize()
                {
                    Configuration.Modules.AbpEfCore().AddDbContext<AcmeDbContext>(options => { });
                    Configuration.Modules.AbpAspNetCore().CreateControllersForAppServices(typeof(AcmeApplicationModule).GetAssembly());
                    Configuration.Modules.AbpWebCommon().SendAllExceptionsToClients = true;
                    Configuration.DefaultNameOrConnectionString = "Default";
                }
            }
            """;

        var (code, _) = Migrate(source);

        AssertContains("context.Services.AddAbpDbContext<AcmeDbContext>(options => { options.AddDefaultRepositories(includeAllEntities: true); });", code);
        AssertContains("Configure<AbpDbContextOptions>(options => { options.UseSqlServer(); });", code);
        AssertContains("options.ConventionalControllers.Create(typeof(AcmeApplicationModule).Assembly);", code);
        AssertContains("Configure<AbpExceptionHandlingOptions>(options => { options.SendExceptionsDetailsToClients = true; });", code);
        AssertNotContains("DefaultNameOrConnectionString", code);
    }

    [Fact]
    public void Localization_configurer_becomes_json_resource_with_languages()
    {
        const string source = """
            using Abp.Modules;

            public class AcmeCoreModule : AbpModule
            {
                public override void PreInitialize()
                {
                    AcmeLocalizationConfigurer.Configure(Configuration.Localization);
                }
            }
            """;

        var (code, _) = Migrate(source, localization: PhoneBookResource);

        AssertContains("options.FileSets.AddEmbedded<AcmeCoreModule>();", code);
        AssertContains("options.Resources.Add<PhoneBookResource>(\"en\").AddVirtualJson(\"/Localization/PhoneBook\");", code);
        AssertContains("options.Languages.Add(new LanguageInfo(\"fa\", \"fa\",", code);
        AssertContains("using Acme.PhoneBook.Localization;", code);
    }

    [Fact]
    public void Localization_in_another_project_is_not_configured_twice()
    {
        const string source = """
            using Abp.Modules;

            public class AcmeCoreModule : AbpModule
            {
                public override void PreInitialize()
                {
                    AcmeLocalizationConfigurer.Configure(Configuration.Localization);
                }
            }
            """;

        var sharedResource = PhoneBookResource with { ProjectName = "Acme.Domain.Shared" };
        var (code, _) = Migrate(source, localization: sharedResource);

        AssertContains("// AbpToVolo: localization (PhoneBookResource) is configured in the Acme.Domain.Shared module.", code);
        AssertNotContains("AddVirtualJson", code);
    }

    [Fact]
    public void Module_constructor_injection_becomes_fields_set_in_PreConfigureServices()
    {
        const string source = """
            using Abp.Modules;

            public class AcmeWebHostModule : AbpModule
            {
                private readonly IWebHostEnvironment _env;
                private readonly IConfigurationRoot _appConfiguration;

                public AcmeWebHostModule(IWebHostEnvironment env)
                {
                    _env = env;
                    _appConfiguration = env.GetAppConfiguration();
                }

                public override void PostInitialize()
                {
                    base.PostInitialize();
                    var url = _appConfiguration["App:Url"];
                }
            }
            """;

        var (code, _) = Migrate(source);

        Assert.DoesNotContain("public AcmeWebHostModule(", code);
        Assert.DoesNotContain("base.PostInitialize()", code);
        AssertContains("private IConfiguration _appConfiguration;", code);
        AssertContains("_env = context.Services.GetSingletonInstance<IWebHostEnvironment>();", code);
        AssertContains("_appConfiguration = context.Services.GetConfiguration();", code);
        AssertContains("public override void PreConfigureServices(ServiceConfigurationContext context)", code);
        AssertContains("var url = _appConfiguration[\"App:Url\"];", code);
    }

    [Fact]
    public void Module_constructor_with_other_services_gets_todo()
    {
        const string source = """
            using Abp.Modules;

            public class AcmeWebHostModule : AbpModule
            {
                public AcmeWebHostModule(IFooService foo)
                {
                }
            }
            """;

        var (code, _) = Migrate(source);

        AssertContains("// TODO [AbpToVolo]: ABP.io creates modules with a parameterless constructor", code);
    }
}
