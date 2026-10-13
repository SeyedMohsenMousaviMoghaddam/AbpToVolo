namespace AbpToVolo.Tests;

public class ServiceRewriterTests : RewriterTestBase
{
    [Fact]
    public void AsyncCrudAppService_is_converted_to_CrudAppService()
    {
        const string source = """
            using System.Linq;
            using System.Threading.Tasks;
            using Abp.Application.Services;
            using Abp.Domain.Repositories;

            public class UserAppService : AsyncCrudAppService<User, UserDto, long, PagedUserResultRequestDto, CreateUserDto, UserDto, EntityDto<long>, EntityDto<long>>, IUserAppService
            {
                public UserAppService(IRepository<User, long> repository) : base(repository)
                {
                    GetAllPermissionName = "Users";
                    DeletePermissionName = "Users.Delete";
                }

                public override async Task<PagedResultDto<UserDto>> GetAllAsync(PagedUserResultRequestDto input)
                {
                    var query = CreateFilteredQuery(input);
                    return await base.GetAllAsync(input);
                }

                protected override IQueryable<User> CreateFilteredQuery(PagedUserResultRequestDto input)
                {
                    return Repository.GetAll().Where(u => u.IsActive);
                }

                protected override UserDto MapToEntityDto(User user) => base.MapToEntityDto(user);
            }
            """;

        var (code, _) = Migrate(source);

        AssertContains("public class UserAppService : CrudAppService<User, UserDto, long, PagedUserResultRequestDto, CreateUserDto, UserDto>, IUserAppService", code);
        AssertContains("not supported by ABP.io's CrudAppService", code);
        AssertContains("GetListPolicyName = \"Users\";", code);
        AssertContains("DeletePolicyName = \"Users.Delete\";", code);
        AssertContains("public override async Task<PagedResultDto<UserDto>> GetListAsync(PagedUserResultRequestDto input)", code);
        AssertContains("var query = (await CreateFilteredQueryAsync(input));", code);
        AssertContains("return await base.GetListAsync(input);", code);
        AssertContains("protected override async Task<IQueryable<User>> CreateFilteredQueryAsync(PagedUserResultRequestDto input)", code);
        AssertContains("return (await Repository.GetQueryableAsync()).Where(u => u.IsActive);", code);
        AssertContains("protected override UserDto MapToGetOutputDto(User user) => base.MapToGetOutputDto(user);", code);
    }

    [Fact]
    public void Crud_interface_arity_is_trimmed()
    {
        const string source = """
            using Abp.Application.Services;
            public interface IUserAppService : IAsyncCrudAppService<UserDto, long, PagedUserResultRequestDto, CreateUserDto, UserDto, EntityDto<long>, EntityDto<long>> { }
            """;

        var (code, _) = Migrate(source);

        AssertContains("public interface IUserAppService : ICrudAppService<UserDto, long, PagedUserResultRequestDto, CreateUserDto, UserDto>", code);
    }

    [Fact]
    public void Repository_calls_are_converted()
    {
        const string source = """
            using System.Linq;
            using System.Threading.Tasks;
            using Abp.Domain.Repositories;

            public class PersonManager
            {
                private readonly IRepository<Person> _personRepository;

                public async Task<int> CreateAsync(Person person)
                {
                    var people = await _personRepository.GetAllListAsync(p => p.IsActive);
                    var first = await _personRepository.FirstOrDefaultAsync(p => p.Name == "x");
                    var total = await _personRepository.CountAsync();
                    var active = await _personRepository.CountAsync(p => p.IsActive);
                    var query = _personRepository.GetAllIncluding(p => p.Phones).Where(p => p.IsActive);
                    return await _personRepository.InsertAndGetIdAsync(person);
                }

                public IQueryable<Person> Query() => _personRepository.GetAll();

                public void Save(Person person)
                {
                    _personRepository.Insert(person);
                }
            }
            """;

        var (code, _) = Migrate(source);

        AssertContains("private readonly IRepository<Person, int> _personRepository;", code);
        AssertContains("await _personRepository.GetListAsync(p => p.IsActive);", code);
        AssertContains("await _personRepository.FindAsync(p => p.Name == \"x\");", code);
        AssertContains("await _personRepository.GetCountAsync();", code);
        AssertContains("await AsyncExecuter.CountAsync((await _personRepository.GetQueryableAsync()).Where(p => p.IsActive));", code);
        AssertContains("(await _personRepository.WithDetailsAsync(p => p.Phones)).Where(p => p.IsActive);", code);
        AssertContains("return (await _personRepository.InsertAsync(person, autoSave: true)).Id;", code);
        AssertContains("// TODO [AbpToVolo]: GetAll() became GetQueryableAsync()", code);
        AssertContains("// TODO [AbpToVolo]: Synchronous repository method 'Insert' does not exist in ABP.io", code);
    }

    [Fact]
    public void Session_and_base_service_members_are_converted()
    {
        const string source = """
            using Abp.Runtime.Session;
            using Abp.Domain.Uow;

            public class MyAppService : AcmeAppServiceBase
            {
                private readonly IAbpSession _abpSession;

                public async Task Do()
                {
                    var userId = AbpSession.UserId;
                    var tenantId = AbpSession.TenantId;
                    var id = AbpSession.GetUserId();
                    var other = _abpSession.UserId;
                    var granted = await PermissionChecker.IsGrantedAsync("P");
                    var theme = await SettingManager.GetSettingValueAsync("Theme");
                    using (CurrentUnitOfWork.DisableFilter(AbpDataFilters.SoftDelete))
                    {
                    }
                    await EventBus.TriggerAsync(new MyEvent());
                }
            }
            """;

        var (code, _) = Migrate(source);

        AssertContains("private readonly ICurrentUser _abpSession;", code);
        AssertContains("var userId = CurrentUser.Id;", code);
        AssertContains("var tenantId = CurrentTenant.Id;", code);
        AssertContains("var id = CurrentUser.GetId();", code);
        AssertContains("var other = _abpSession.Id;", code);
        AssertContains("await AuthorizationService.IsGrantedAsync(\"P\");", code);
        AssertContains("await SettingManager.GetOrNullForCurrentUserAsync(\"Theme\");", code);
        AssertContains("using (DataFilter.Disable<ISoftDelete>())", code);
        AssertContains("await LocalEventBus.PublishAsync(new MyEvent());", code);
        AssertContains("using Volo.Abp.Users;", code);
    }

    [Fact]
    public void Static_clock_outside_services_becomes_DateTime()
    {
        const string source = """
            using Abp.Timing;
            public class Order : Entity
            {
                public Order() { CreatedAt = Clock.Now; }
                public System.DateTime CreatedAt { get; set; }
            }
            """;

        var (code, _) = Migrate(source);

        AssertContains("CreatedAt = DateTime.Now;", code);
        AssertContains("public class Order : Entity<int>", code);
        AssertContains("using System;", code);
    }

    [Fact]
    public void Castle_logger_becomes_microsoft_logger()
    {
        const string source = """
            using Castle.Core.Logging;

            public class Worker
            {
                public ILogger Logger { get; set; } = NullLogger.Instance;

                public void Run(System.Exception ex)
                {
                    Logger.Info("start");
                    Logger.Error("failed", ex);
                }
            }
            """;

        var (code, _) = Migrate(source);

        AssertContains("using Microsoft.Extensions.Logging;", code);
        AssertContains("using Microsoft.Extensions.Logging.Abstractions;", code);
        AssertContains("Logger.LogInformation(\"start\");", code);
        AssertContains("Logger.LogError(ex, \"failed\");", code);
        AssertNotContains("Castle", code);
    }

    [Fact]
    public void Event_handlers_become_local_event_handlers()
    {
        const string source = """
            using Abp.Events.Bus.Handlers;
            using Abp.Events.Bus.Entities;

            public class PersonCreatedHandler : IEventHandler<EntityCreatedEventData<Person>>
            {
                public void HandleEvent(EntityCreatedEventData<Person> eventData)
                {
                }
            }
            """;

        var (code, _) = Migrate(source);

        AssertContains("public class PersonCreatedHandler : ILocalEventHandler<EntityCreatedEventData<Person>>", code);
        AssertContains("public async Task HandleEventAsync(EntityCreatedEventData<Person> eventData)", code);
        AssertContains("using Volo.Abp.Domain.Entities.Events;", code);
    }

    [Fact]
    public void Startup_and_program_are_converted()
    {
        const string source = """
            using System;
            using Abp.AspNetCore;
            using Castle.Facilities.Logging;

            public class Startup
            {
                public IServiceProvider ConfigureServices(IServiceCollection services)
                {
                    return services.AddAbp<AcmeWebHostModule>(options => options.IocManager.IocContainer.AddFacility<LoggingFacility>());
                }

                public void Configure(IApplicationBuilder app)
                {
                    app.UseAbp(options => { options.UseAbpRequestLocalization = false; });
                }

                public static IHostBuilder Create(string[] args) =>
                    Host.CreateDefaultBuilder(args).UseCastleWindsor(IocManager.Instance.IocContainer);
            }
            """;

        var (code, _) = Migrate(source);

        AssertContains("public void ConfigureServices(IServiceCollection services)", code);
        AssertContains("services.AddApplication<AcmeWebHostModule>();", code);
        AssertContains("app.InitializeApplication();", code);
        AssertContains("Host.CreateDefaultBuilder(args).UseAutofac();", code);
        AssertNotContains("using Castle", code);
        AssertNotContains("IocManager", code);
    }
}
