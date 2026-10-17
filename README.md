# AbpToVolo

**English** | [فارسی](README.fa.md)

Automated migration tool to help upgrade **ASP.NET Boilerplate** (`Abp.*`, including Module Zero) solutions to the modern **ABP Framework** (`Volo.Abp.*`, abp.io), following the layout and naming of the abp.io layered template. The input folder is never modified: the converted solution is written to a separate folder.

> AbpToVolo is an independent open-source project. It is not affiliated with or endorsed by Volosoft or the ASP.NET Boilerplate / ABP Framework teams. "ABP" and "Volo" are trademarks of their respective owners.

The code is converted with **Roslyn** (C# syntax trees and semantic model), not with text replacement. After the conversion, the output is built with the real compiler and the errors that follow known patterns are fixed in place (*compiler-guided fixes*). Everything that cannot be converted automatically is marked with `// TODO [AbpToVolo]: ...` and listed, with file and line, in the migration report.

## Getting started

Requires the .NET 10 SDK.

| Way | Command |
|---|---|
| Command line | `dotnet run --project src/AbpToVolo -- "C:\src\MyApp" -o "C:\src\MyApp.AbpIo"` |
| dotnet tool | `dotnet pack src/AbpToVolo -c Release -o ./nupkg`, then `dotnet tool install -g AbpToVolo --add-source ./nupkg` and `abp-to-volo ...` |
| Windows app (Persian UI, RTL) | `dotnet run --project src/AbpToVolo.Desktop` |

The Windows app sets the source and destination folders, the ABP version, target framework, migration mode and options. It shows every step with its status, duration, changes and manual items, a live colored log, overall/step progress bars and a cancel button, and opens the output folder, report and log when done. Settings are kept in `%AppData%\AbpToVolo\settings.json` and can be saved/loaded as files (same format as `--config`).

| Option | Description |
|---|---|
| `-o, --output` | Output folder (required) |
| `--config <file>` / `--save-config <file>` | Read / save the settings as JSON (same file as the Windows app) |
| `--abp-version`, `--framework`, `--ms-version` | `Volo.Abp.*` version (default `10.7.0`), target framework (default `net10.0`), Microsoft packages version (default: read from ABP's own nuspec) |
| `--keep-layer-names` | Keep the Boilerplate project names (default: abp.io names) |
| `--no-zero` | Do not replace Module Zero with the ABP.io modules |
| `--no-host` | Do not generate the abp.io HttpApi.Host and DbMigrator |
| `--no-contracts` / `--no-tests` | Do not create the contract projects / the test projects |
| `--migrations squash\|delete\|keep` | Squash the EF Core migrations keeping the history (default), delete them, or keep them as they are |
| `--no-compiler-fixes` | Do not build the output and fix compiler errors |
| `--stub-unresolved` | Members that still do not compile keep their converted body under `#if ABPTOVOLO_ORIGINAL` and throw `NotImplementedException`, so the whole solution compiles (each one is listed in the report) |
| `--id-type source\|guid` | Key type of the application's references to users/roles (`UserId`, `RoleId`…): `source` (default) keeps the source type (long/int), linked to the ABP.io entities through `LegacyId`; `guid` converts them to Guid. ABP.io module fields (`TenantId`, `CreatorId`…) are Guid in both |
| `--api-style boilerplate\|abp` | `boilerplate` (default): the converted host keeps the HTTP API of the source — `/api/services/app/{Service}/{Action}` routes and the `AjaxResponse` envelope — so the existing front end works unchanged; `abp`: ABP.io conventions (`/api/app/{service}`, results not wrapped) |
| `--connection-string <cs>` | `ConnectionStrings:Default` of the converted host and DbMigrator (or `ABPTOVOLO_CONNECTION_STRING`; never saved with the settings) |
| `--migrate-database` | Run the converted DbMigrator after the conversion (**changes that database — use a copy**) |
| `--verify-host` | Start the converted host, check that Swagger answers (`/swagger/v1/swagger.json`, `/swagger/index.html`), compare its endpoints with the source's, stop it; the result is in the report |
| `--build`, `--force`, `--verbose` | Final build with the errors in the report, replace a non-empty output, full log |

Output: the converted solution, `ABP-MIGRATION-REPORT.md` (checklist of the manual work, with lines), `abp-migration-report.json` and `abp-migration.log` (full log of every step).

## Updating the output when the source evolves (git)

The converted solution is usually edited by hand afterwards. To bring later changes of the Boilerplate source into it without losing those edits, use git's three-way merge — AbpToVolo makes this work by producing **deterministic** output: the same source always gives the same files (generated migration ids, string-encryption passphrase, `.sln` project GUIDs and the migration squash point are kept in `.abptovolo/state.json`), and `--force` keeps `.git` and `.abptovolo`.

**First time:**

```bash
abp-to-volo C:\src\MyApp -o C:\src\MyApp.AbpIo
cd C:\src\MyApp.AbpIo
git init -b converted && git add -A && git commit -m "converted: MyApp <commit>"
git checkout -b main          # manual changes on main only
```

**Every time the source changes:**

```bash
git checkout converted
abp-to-volo C:\src\MyApp -o C:\src\MyApp.AbpIo --force
git add -A && git commit -m "converted: MyApp <commit>"
git checkout main
git merge converted           # only real conflicts remain; resolve them with git / Visual Studio as usual
```

- `converted` always holds the untouched output of the tool; the diff between two of its commits is exactly the new source changes, in ABP Framework form.
- **Migrations:** the squash point of the first conversion never moves (deployed databases know only that id) and the model snapshot stays there. Migrations added to the source later are not copied; they are listed in the report (with a warning when they run custom SQL). Create the schema change on `main` after the merge with `dotnet ef migrations add <Name>` — EF Core derives it from the converted entities — and copy their custom SQL, if any.
- The `.gitignore` of the output ignores `bin`/`obj`, the log and the JSON report, which change on every run.

## Conversion steps

1. Copy source → 2. Detect solution and layers → 3. Rename layers → 4. ABP.io module names → 5. Contract projects → 6. Localization (XML → JSON) → 7. EF Core migrations → 8. C# code → 9. Module Zero → ABP.io modules → 10. Service base classes → 11. Move code to the contracts → 12. ABP.io modules (Identity, Account, OpenIddict…) → 13. Layer services (HttpClient, SignalR) → 14. HttpApi.Host and DbMigrator → 15. Test projects → 16. Compatibility shims → 17. Package versions → 18. Project files → 19. Compiler-guided fixes → 20. Residual scan → 21. Build verification → 22. Report

## abp.io conventions

| ASP.NET Boilerplate | ABP Framework |
|---|---|
| `X.Core` / `XCoreModule` | `X.Domain` / `XDomainModule` |
| `X.Application` | `X.Application` + `X.Application.Contracts` |
| — | `X.Domain.Shared` / `XDomainSharedModule` |
| `X.EntityFrameworkCore` / `XEntityFrameworkModule` | `X.EntityFrameworkCore` / `XEntityFrameworkCoreModule` |
| `X.Web.Core` / `XWebCoreModule` | `X.HttpApi` / `XHttpApiModule` |
| `X.Web.Host` (Startup, AuthConfigurer) | `X.HttpApi.Host` / `XHttpApiHostModule` + minimal-hosting `Program.cs` with Serilog, OpenIddict validation, Swagger OAuth, CORS |
| `X.Migrator` | `X.DbMigrator` (`XDbMigratorModule`, `DbMigratorHostedService`, `XDbMigrationService`, `IXDbSchemaMigrator`) |
| `XAppServiceBase` / `AbpServiceBase` | `XAppService : ApplicationService`, `XDomainService : DomainService`, `XAsyncBackgroundJob<TArgs>` |

The generated base classes provide the members Boilerplate's base classes had (`L`, `LocalizationManager`, `SettingManager`, `CurrentUnitOfWork`, `DataFilter`…) through `LazyServiceProvider`, so services and jobs compile without large changes.

**Host:**
- `Startup` is replaced by an abp.io-style `XHttpApiHostModule`.
- The old host module, which holds application logic (Quartz job schedules, settings, connections), is kept and compiled as `XHttpApiHostCustomModule`; the new module depends on it.
- SignalR hubs keep their routes (`MapHub<T>("/route")` in `UseConfiguredEndpoints`).
- The other custom registrations/middlewares of `Startup` are listed as TODO comments in the module (also when `Startup` was already moved to `_BoilerplateLegacy`).
- Module constructors that received `IWebHostEnvironment`/`IConfiguration` (ABP.io creates modules with a parameterless constructor) become fields set in `PreConfigureServices`; `base.PreInitialize()`/`PostInitialize()` calls are removed.

**Layer services:** in Boilerplate the web host registered `IHttpClientFactory`, `IHubContext<T>` and `IHttpContextAccessor`; in ABP.io each module registers what its layer injects (`AddHttpClient()`, `AddSignalR()`, `AddHttpContextAccessor()`), so the layer also works in tests and in the DbMigrator. Classes whose name does not match their interface (ABP.io only exposes matching interfaces) get `[ExposeServices(..., IncludeDefaults = true, IncludeSelf = true)]`.

## Module Zero

**Module Zero** is Boilerplate's `Abp.Zero.*` package: users, roles, tenants, permissions, settings, organization units, login… Applications derive from its base classes (`User : AbpUser<User>`, `Role : AbpRole<User>`, `Tenant : AbpTenant<User>`, `UserManager : AbpUserManager<Role, User>`, `XDbContext : AbpZeroDbContext<...>`).

ABP Framework has **no** Module Zero: the same features come from separate modules.

| Module Zero | ABP Framework |
|---|---|
| User, Role, UserManager, RoleManager, OrganizationUnit | **Identity** (`IdentityUser`, `IdentityRole`, `IdentityUserManager`…) |
| Tenant, TenantManager, Edition | **TenantManagement** and **FeatureManagement** |
| PermissionChecker, UserPermissionSetting/RolePermissionSetting | **PermissionManagement** (`PermissionGrant`) |
| SettingManager, Setting | **SettingManagement** |
| LogInManager, TokenAuthController, JWT | **Account** + **OpenIddict** |

So the application's Module Zero classes are not ported; the code is connected to these modules:
- References are replaced: `User` → `IdentityUser`, `IRepository<User, long>` → `IRepository<IdentityUser, Guid>`…
- Custom properties of User/Role/Tenant become **module entity extensions** (`XModuleExtensionConfigurator`, `XEfCoreEntityExtensionMappings`) used with `GetProperty`/`SetProperty`; constants move to `{Name}Consts` classes.
- Boilerplate template classes (UserManager, LogInManager, SessionAppService…) move to `_BoilerplateLegacy`, excluded from the build.
- The DbContext is integrated with `[ReplaceDbContext(typeof(IIdentityDbContext), typeof(ITenantManagementDbContext))]` and `ConfigureIdentity()`/`ConfigureOpenIddict()`…
- **Database** (SQL Server), keeping the data:
  1. Migration `RenameModuleZeroTables` renames the Module Zero tables (and their PK/FK/index names) to `Zero_*`, so the ABP.io tables can be created next to them.
  2. Migration `ConvertModuleZeroKeys` builds `Zero_IdMap` (old id → new Guid) and converts the columns of the **application's own tables** through it: user/tenant/role foreign keys (`UserId bigint`, `TenantId int`, `RoleId int` → `uniqueidentifier`) and the audit columns (`CreatorUserId` → `CreatorId`, `LastModifierUserId` → `LastModifierId`, `DeleterUserId` → `DeleterId`). Old FKs are dropped and indexes recreated; an id without a mapping stops the migration with a clear message. The snapshot is updated too, so the next migration (`AbpIoModules`) only adds the ABP.io tables and the new FKs instead of dropping columns.
  3. Script `ModuleZeroDataMigration.sql` copies users, roles, tenants, user roles and permissions into the ABP.io tables with **the same `Zero_IdMap` Guids** (password hashes are compatible: both use ASP.NET Core Identity).
- The code follows the same id rule (`ZeroMappings.GuidIdKind`): `*UserId`/`*TenantId`/`*RoleId`/`*OrganizationUnitId` properties, parameters and variables become `Guid`, and `[ForeignKey("CreatorUserId")]`/`HasForeignKey("…")` strings get the ABP.io names, so the EF model and the database stay consistent.

### Key types (`--id-type`)

ABP.io's Identity and TenantManagement modules use Guid keys. The module fields follow that standard in both modes (`TenantId`, `CreatorId`/`LastModifierId`/`DeleterId`, converted with their data through `Zero_IdMap`). For the application's **own** references to users, roles and organization units:

| | `source` (default) | `guid` |
|---|---|---|
| `UserId`, `OwnerUserId`, `RoleId`… in code and database | keep the source type (long/int) | Guid (data converted) |
| Link to `IdentityUser`/`IdentityRole` | `LegacyId` extension property/column (old id; new rows numbered by a sequence), `HasPrincipalKey("LegacyId")` for every navigation with a long/int key | the Guid key |
| Current user | `CurrentUser.GetLegacyId()` / `GetRequiredLegacyId()` (claim `legacy_user_id`) | `CurrentUser.Id` |
| Find a user by the old id | `ILegacyIdLookup.FindUserAsync(long)`, `user.GetLegacyId()` | — |
| Tenant ids in the application's code and API | int, as in the source: `Tenant.LegacyId` (old id; new tenants numbered by a sequence). `tenant.Id` used as an int becomes `EF.Property<int>(t, "LegacyId")` in EF Core queries and `tenant.GetLegacyId()` elsewhere; `repository.DeleteAsync(intId)` becomes a `LegacyId` predicate; the `Abp.TenantId` header/cookie of Boilerplate clients selects the tenant with that `LegacyId` | Guid |

Tenants: Boilerplate has `TenancyName` (identifier) and `Name` (display name), ABP.io only `Name`. `Name` keeps the display name (at most 64 characters, made unique with the tenancy name if needed), and `TenancyName` and `IsActive` are kept as tenant extensions on the same data, so `tenant.Name` and `tenant.TenancyName` keep their meaning in the code.

Entity extensions (`GetProperty`, `GetLegacyId()`) read inside EF Core queries cannot be translated to SQL: once the solution compiles, they are rewritten to `EF.Property<T>(x, "Name")`, using the semantic model of the converted projects with their real references.

DTOs and models keep the source id types (tenant `int`, user `long`), so the API does not change; the entities use the ABP.io ids (Guid). Where the two meet (`entity.TenantId == input.TenantId`, `dto.UserId = user.Id`, `CurrentTenant.Id`…), the compiler-guided fixes insert `LegacyTenantIds.ToTenantGuid / ToLegacyTenantId` and `LegacyUserIds.ToUserGuid / ToLegacyUserId` (Domain.Shared; cached, resolved from `LegacyId` in the database).

### HTTP API (`--api-style`)

Every endpoint of the source stays, at the same path and HTTP verb, so the front end keeps working:

- Application services and controllers are never moved to the legacy folder, even the template ones (`UserAppService`, `TenantAppService`, `SessionAppService`, `TokenAuthController`…). What does not compile is stubbed member by member (a constructor parameter or field that does not resolve is removed, an unknown type in a signature becomes `object`, a method body throws `NotImplementedException`), so the route exists and the report lists what to finish.
- `boilerplate` (default) generates `BoilerplateApi` in the HttpApi project: an `IConventionalRouteBuilder` replacement for the `/api/services/app/{Service}/{Action}` routes (same verb rules: `Get…` → GET, `Update…` → PUT, `Delete…` → DELETE, otherwise POST), and a result filter that returns results, errors (`RemoteServiceErrorResponse`) and 401/403 answers as `{ result, targetUrl, success, error, unAuthorizedRequest, __abp }`. `[DontWrapResult]`/`[WrapResult]` keep working (compatibility types in Domain.Shared). The ABP.io module endpoints (`/api/abp/…`, `/api/identity/…`, `/connect/token`) are unchanged.
- `--verify-host` compares the endpoints of the source (Boilerplate conventions) with the converted `swagger.json` and lists the missing ones and the changed verbs ("Endpoints of the source" in the report).
- The TokenAuth login keeps working: `TokenAuthConfiguration` and its configuration stay (registered as a singleton), the host accepts its JWT next to OpenIddict (`BoilerplateJwtBearer`: signed tokens of the configured issuer, and `enc_auth_token` for SignalR), and the Boilerplate login services are generated on top of ABP.io: `LogInManager.LoginAsync` → `AbpLoginResult` (IdentityUserManager, same password hashes), `AbpLoginResultTypeHelper`, `ITenantCache`, `IClientInfoProvider`, `SimpleStringCipher` (same algorithm and defaults) and the `ISettingManager` methods `GetSettingValueForApplication<T>` / `…ForTenant` / `…ForUser` / `ChangeSettingFor…`.
- Localization: `LocalizationManager.GetAllSources()` / `GetSource(name)` (name, `GetString`, `GetAllStrings(culture)`) over the ABP.io localization resources.

### SQL views

Views (raw SQL of the old migrations) reference the Module Zero tables and the audit columns. `RenameModuleZeroTables` saves their definitions in `Zero_ViewDefinitions` and drops them; `AbpIoModules` recreates them (`Zero_RecreateViews`, audit column names updated, dependent views retried). A view that still fails is kept with its error in `Zero_ViewDefinitions` and listed in the report. Entity types mapped to views (`ToView`) are never altered as tables by the generated migration.

## Contract projects (`Domain.Shared`, `Application.Contracts`)

| Target | Content |
|---|---|
| `X.Domain.Shared` | enums, constant classes (`PermissionNames`, `AppConsts`…), simple DTOs of the domain project, localization resources (JSON) |
| `X.Application.Contracts` | DTOs, application service interfaces, enums of the application layer, `PermissionDefinitionProvider` |

- The unit that moves is a type, not a file (namespaces are kept).
- A type moves only when all its dependencies are allowed in the target layer; the others stay, with the reason in the report.
- `[AutoMap(typeof(Entity))]` on moved DTOs becomes `CreateMap<,>()` in a generated profile; `Entity.MaxNameLength` becomes `EntityConsts.MaxNameLength`.

## Migration squash (history kept)

- All migrations are squashed into `SquashedBoilerplateMigrations`, which **keeps the id of the last migration**: existing databases see it as applied and their `__EFMigrationsHistory` is untouched.
- The `Up()`/`Down()` bodies are copied unchanged, in their original order, into one file per year, so a new database gets exactly the same schema. Only the last designer is kept.
- `MIGRATION-HISTORY.md` lists the squashed migrations; `squash-precheck.sql` checks that a database is at the last migration before deploying.

## Generated abp.io (Volo) tests

| Project | Content |
|---|---|
| `X.TestBase` | `XTestBaseModule` (Autofac, always-allow authorization, background jobs off, data seed), `XTestBase<TModule>` with `WithUnitOfWorkAsync`, `XTestConsts`, `FakeCurrentPrincipalAccessor` |
| `X.Domain.Tests` | `XDomainTestModule`, abstract tests for every domain service, and the abp.io `SampleDomainTests` (users with `IdentityUserManager`) |
| `X.Application.Tests` | `XApplicationTestModule`, abstract tests for every application service, `SampleAppServiceTests` (`IIdentityUserAppService`) |
| `X.EntityFrameworkCore.Tests` | `XEntityFrameworkCoreTestModule` with in-memory SQLite, `XEntityFrameworkCoreFixture`/collection, `SampleRepositoryTests`, a query test per `DbSet` and EF Core versions of all abstract tests |

Real models often contain SQL Server SQL (computed columns, `HasDefaultValueSql`, filtered indexes, check constraints) that SQLite cannot run: the generated `SqliteTestModelCustomizer` removes it from the test database model only. Spatial data (NetTopologySuite) is supported with `UseNetTopologySuite()` and `Microsoft.EntityFrameworkCore.Sqlite.NetTopologySuite`.

## Compiler-guided fixes

The output is built up to 10 times; errors with a known pattern are fixed with Roslyn at the exact position, for example:
- `IdentityUser`/`IdentityRole`/`Tenant` members: renames (`EmailAddress` → `Email`), former custom properties → `GetProperty`/`SetProperty`, `FullName` → `Name + Surname`.
- Removed repository methods (sync → async, `GetAll` → `GetQueryableAsync`, `InsertRangeAsync` → `InsertManyAsync`…).
- Protected setters: `entity.Id = x` → `EntityHelper.TrySetId`, `new E { Id = x }` → `.SetProtected(e => e.Id, x)`, user setters → `SetPhoneNumber`/`SetIsActive`…
- Members Boilerplate base classes provided (`CurrentTenant`, `CurrentUser`, `DataFilter`, `AuthorizationService`, `CurrentUnitOfWork`…) are declared as property-injected members in classes without an ABP.io base class.
- `LocalizedString` used as string → `.Value`; `long.Parse(userId)` where a Guid is expected → `Guid.Parse`; `uow.SetTenantId` → `CurrentTenant.Change`.
- `[UnitOfWork(isolationLevel)]`, `AsyncCrudAppService` overrides → `CrudAppService` signatures, `IRepository<BackgroundJobInfo, long>` → `IRepository<BackgroundJobRecord, Guid>`, `UserFriendlyException(msg, ex)` → `innerException: ex`, `SimpleStringCipher.DefaultPassPhrase` → `AbpStringEncryptionOptions`, unused usings of removed namespaces.
- NU1605 (package downgrade) → the required version.

With `--stub-unresolved`, when no more errors can be fixed, the members that still fail (method, constructor, accessor) keep their converted body under `#if ABPTOVOLO_ORIGINAL` and throw `NotImplementedException`; when the error is in a declaration (field type, base class), the whole file goes under `#if` — except modules and DbContexts. The build then continues so the next projects (Application, HttpApi, Host, tests) are checked and fixed as well. No code is deleted; the full list is in the "Members kept under #if ABPTOVOLO_ORIGINAL" section of the report.

## Compatibility shims

Generated in the domain project (`AbpToVoloCompat` folder) only when the code uses these APIs, and listed in the report so they can be replaced over time: `ObjectMapper.Map<TDest>(src)`, `JobBase` (Quartz), `IExtendableObject`, `ILocalizationManager.GetString(source, key)`, `ICacheManager`/`ITypedCache` (on `IDistributedCache`), `SetProtected`.

Custom repositories that run raw ADO.NET or stored procedures through `IActiveTransactionProvider` are converted to the ABP.io way: `(await GetDbContextAsync()).Database.CurrentTransaction?.GetDbTransaction()` and `Database.GetDbConnection()`. Identity entities that are not aggregate roots (`IdentityUserRole`…) but are used through repositories get a `DbSet` in the application DbContext.

## Manual work (listed in the report)

- Hard-coded numeric user/tenant ids in code (e.g. `tenantId = 1`): they must be Guids in ABP.io (see `Zero_IdMap`).
- Navigation properties on Module Zero entities (e.g. `User.Customer`): the Identity module does not allow navigations on `IdentityUser`; load them through a repository or a query.
- Custom user creation with `new User { ... }` and setters → `new IdentityUser(id, userName, email, tenantId)` + `IdentityUserManager.CreateAsync`.
- Custom methods of the application's Module Zero classes (e.g. `UserManager.GetCustomerIdFromUserId`): move them to a domain service (the old code is in `_BoilerplateLegacy`).
- `AuditLog`/`EntityChange` (AuditLogging module), `IAbpAntiForgeryManager`, `IClientInfoProvider`, `IErrorInfoBuilder` and custom host exception handlers.
- `EmailConfirmationCode`/`PasswordResetCode` (Identity tokens in ABP.io), `SimpleStringCipher` (`IStringEncryptionService`).
- The front end (Angular/Vue/MVC) is not converted.

## Project structure

```
src/AbpToVolo.Engine/   conversion engine (library)
  Core/                 options, settings, context, observer and logging, report, build
  Mappings/             namespace / package / module / type / Module Zero mappings
  CodeMigration/        Roslyn rewriters (one per kind of conversion)
  Steps/                the 22 conversion steps
src/AbpToVolo/          command line (dotnet tool: abp-to-volo)
src/AbpToVolo.Desktop/  Windows app (WinForms)
tests/AbpToVolo.Tests/  unit tests of the rewriters + end-to-end tests on the sample
samples/Acme.PhoneBook/ sample ASP.NET Boilerplate solution (with Module Zero)
```

To add a conversion, create a `MigrationRewriter` and add it to `CSharpFileMigrator.Pipeline`, or extend the tables in `Mappings/`. A new step implements `IMigrationStep` and is added to `MigrationEngine.DefaultSteps()`.

## Validation

```bash
dotnet test
dotnet run --project src/AbpToVolo -- samples/Acme.PhoneBook -o ../out/PhoneBook --force --build
```

- 90 tests, including one that checks that converting the same source again gives exactly the same output.
- The `Acme.PhoneBook` sample (with Module Zero) builds without errors after the conversion with ABP 10.7.0, and all 20 generated tests pass, including the abp.io sample tests. The only warning is NU1903: AutoMapper 14, used by `Volo.Abp.AutoMapper`, has a reported vulnerability — ABP 10 defaults to Mapperly, and moving to it is recommended.
- The tool has also been run on a large real-world Boilerplate solution (thousands of C# files, hundreds of migrations, Module Zero, SignalR, Quartz, custom repositories): with `--stub-unresolved` the whole converted solution builds without errors and most generated tests pass; what remains is the manual work listed above.

## License

[MIT](LICENSE)
