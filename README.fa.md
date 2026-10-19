# AbpToVolo — تبدیل ASP.NET Boilerplate به ABP Framework (abp.io / Volo)

[English](README.md) | **فارسی**

> این پروژه یک ابزار متن‌باز و مستقل است و ارتباطی با Volosoft یا تیم‌های ASP.NET Boilerplate و ABP Framework ندارد. «ABP» و «Volo» نام‌های تجاری صاحبانشان هستند.

ابزار .NET 10 که سورس یک پروژه **ASP.NET Boilerplate** (`Abp.*`) را می‌گیرد و یک کپی تبدیل‌شده به **ABP Framework** (`Volo.Abp.*`) با ساختار و نام‌گذاری قالب لایه‌ای abp.io تولید می‌کند. سورس ورودی هیچ‌وقت تغییر نمی‌کند.

تبدیل کد با **Roslyn** (درخت نحوی C#) انجام می‌شود، نه جایگزینی متنی. بعد از تبدیل، خروجی با کامپایلر واقعی build می‌شود و خطاهای قابل‌اصلاح خودکار رفع می‌شوند (Compiler-guided fixes). هر چیزی که خودکار قابل تبدیل نیست با `// TODO [AbpToVolo]: ...` در کد علامت می‌خورد و در گزارش نهایی فهرست می‌شود.

## اجرا

سه راه اجرا با تنظیمات مشترک (`MigrationSettings`):

| راه | دستور |
|---|---|
| برنامه ویندوزی (فارسی، راست‌به‌چپ) | `dotnet run --project src/AbpToVolo.Desktop` |
| خط فرمان | `dotnet run --project src/AbpToVolo -- "C:\src\MyProject" -o "C:\out\MyProject.AbpIo"` |
| dotnet tool | `dotnet pack src/AbpToVolo -c Release -o ./nupkg` و بعد `dotnet tool install -g AbpToVolo --add-source ./nupkg` و `abp-to-volo ...` |

در برنامه ویندوزی پوشه مبدا و مقصد، نسخه ABP، framework، حالت migration و گزینه‌ها تنظیم می‌شوند. مراحل در یک فهرست با وضعیت، مدت، تعداد تغییرات و کارهای دستی نمایش داده می‌شوند، لاگ رنگی زنده و دو نوار پیشرفت (کل و مرحله) دارد و امکان لغو هم هست. بعد از اتمام، پوشه خروجی، گزارش و فایل لاگ باز می‌شوند. تنظیمات در `%AppData%\AbpToVolo\settings.json` ذخیره می‌شوند و قابل ذخیره و بارگذاری به‌صورت فایل هم هستند.

| گزینه خط فرمان | توضیح |
|---|---|
| `-o, --output` | پوشه خروجی (الزامی) |
| `--config <file>` / `--save-config <file>` | خواندن یا ذخیره تنظیمات JSON (همان فایل برنامه ویندوزی) |
| `--abp-version`، `--framework`، `--ms-version` | نسخه `Volo.Abp.*` (پیش‌فرض `10.7.0`)، TFM (پیش‌فرض `net10.0`)، نسخه پکیج‌های Microsoft (پیش‌فرض: از nuspec خود ABP) |
| `--keep-layer-names` | نام‌های Boilerplate حفظ شوند (پیش‌فرض: نام‌های abp.io) |
| `--no-zero` | Module Zero به ماژول‌های ABP.io تبدیل نشود |
| `--no-host` | Host و DbMigrator استاندارد abp.io ساخته نشوند |
| `--no-contracts` / `--no-tests` | پروژه‌های Contract / تست ساخته نشوند |
| `--migrations squash\|delete\|keep` | ادغام migrationها با حفظ تاریخچه (پیش‌فرض)، حذف، یا بدون تغییر |
| `--no-compiler-fixes` | خروجی build و اصلاح خودکار نشود |
| `--no-stub` | پیش‌فرض (`--stub-unresolved`): خطاهایی که اصلاح خودکار ندارند: بدنه اصلی عضو زیر `#if ABPTOVOLO_ORIGINAL` نگه داشته می‌شود و `NotImplementedException` پرتاب می‌کند تا کل solution کامپایل شود (هر مورد در گزارش). با `--no-stub` به‌صورت خطای کامپایل باقی می‌مانند |
| `--id-type source\|guid` | نوع کلید ارجاع‌های خود برنامه به کاربر/نقش (`UserId`، `RoleId`…). `source` (پیش‌فرض) نوع مبدا (long/int) را نگه می‌دارد و از طریق `LegacyId` به Entityهای ABP.io وصل می‌شود؛ `guid` آن‌ها را به Guid تبدیل می‌کند. فیلدهای ماژول‌های ABP.io (`TenantId`، `CreatorId`…) در هر دو حالت Guid هستند |
| `--connection-string <cs>` | `ConnectionStrings:Default` برای Host و DbMigrator تبدیل‌شده (یا متغیر محیطی `ABPTOVOLO_CONNECTION_STRING`؛ در تنظیمات ذخیره نمی‌شود) |
| `--migrate-database` | بعد از تبدیل، DbMigrator را اجرا می‌کند (**دیتابیس را تغییر می‌دهد؛ روی کپی اجرا کنید**) |
| `--verify-host` | Host تبدیل‌شده را اجرا می‌کند، بالا آمدن Swagger را بررسی می‌کند، endpointهایش را با مبدا مقایسه می‌کند و آن را می‌بندد؛ نتیجه در گزارش می‌آید |
| `--api-style boilerplate\|abp` | `boilerplate` (پیش‌فرض): Host تبدیل‌شده همان API مبدا را دارد (مسیرهای `/api/services/app/{Service}/{Action}` و پوشش `AjaxResponse`) تا فرانت بدون تغییر کار کند؛ `abp`: قراردادهای ABP.io (`/api/app/{service}` و پاسخ بدون پوشش) |
| `--no-build`، `--force`، `--verbose` | بدون build نهایی (پیش‌فرض: build نهایی و درج خطاها در گزارش)، جایگزینی خروجی (`.git` و `.abptovolo` حفظ می‌شوند)، لاگ کامل |

خروجی: solution تبدیل‌شده + `ABP-MIGRATION-REPORT.md` (چک‌لیست کارهای دستی با شماره خط) + `abp-migration-report.json` + `abp-migration.log` (لاگ کامل همه مراحل).

## به‌روزرسانی مقصد وقتی مبدا توسعه پیدا می‌کند (با git)

مقصد بعد از تبدیل معمولاً دستی اصلاح می‌شود. برای اینکه تغییرات جدید مبدا بدون از دست رفتن این اصلاحات به مقصد برسد، ابزار ادغام جداگانه ندارد. کار با ادغام سه‌طرفه‌ی خود git انجام می‌شود. ابزار فقط تضمین می‌کند خروجی **قطعی** باشد: یک سورس یکسان همیشه همان فایل‌ها را می‌دهد. شناسه migrationهای تولیدی، passphrase، GUID پروژه‌ها در `.sln` و نقطه squash در `.abptovolo/state.json` ثابت می‌مانند. `--force` هم `.git` و `.abptovolo` را پاک نمی‌کند.

**بار اول:**

```bash
abp-to-volo C:\src\MyApp -o C:\src\MyApp.AbpIo
cd C:\src\MyApp.AbpIo
git init -b converted && git add -A && git commit -m "converted: MyApp <commit>"
git checkout -b main          # اصلاحات دستی فقط روی main
```

**هر بار که مبدا تغییر کرد:**

```bash
git checkout converted
abp-to-volo C:\src\MyApp -o C:\src\MyApp.AbpIo --force
git add -A && git commit -m "converted: MyApp <commit>"
git checkout main
git merge converted           # فقط تعارض‌های واقعی می‌مانند؛ با ابزار معمول git / Visual Studio حل می‌شوند
```

- شاخه `converted` همیشه خروجی دست‌نخورده‌ی ابزار است و نباید دستی تغییر کند. تفاوت دو commit آن دقیقاً تغییرات جدید مبدا به شکل Volo است.
- **Migrationها:** نقطه squash بار اول ثابت می‌ماند، چون دیتابیس‌های مستقرشده فقط همان شناسه را می‌شناسند. snapshot هم روی همان نقطه می‌ماند. migrationهایی که بعداً به مبدا اضافه شده‌اند وارد خروجی نمی‌شوند و در گزارش فهرست می‌شوند (اگر SQL دستی دارند، با هشدار). تغییر schema را در `main` بعد از merge بسازید: `dotnet ef migrations add <نام>`. EF آن را از Entityهای تبدیل‌شده می‌سازد. SQL دستی آن migrationها را هم خودتان در migration جدید کپی کنید.
- `.gitignore` خروجی، `bin`/`obj`، لاگ و گزارش JSON را که در هر اجرا عوض می‌شوند، نادیده می‌گیرد.

## مراحل تبدیل

۱. کپی سورس ← ۲. تشخیص solution و لایه‌ها ← ۳. تغییر نام لایه‌ها ← ۴. نام‌های استاندارد ماژول‌ها ← ۵. ساخت Contractها ← ۶. لوکالیزیشن ← ۷. migrationها ← ۸. کد C# ← ۹. Module Zero ← ۱۰. کلاس‌های پایه سرویس ← ۱۱. انتقال کد به Contractها ← ۱۲. ماژول‌های ABP.io ← ۱۳. سرویس‌های هر لایه (HttpClient، SignalR) ← ۱۴. Host و DbMigrator ← ۱۵. پروژه‌های تست ← ۱۶. Shimهای سازگاری ← ۱۷. نسخه پکیج‌ها ← ۱۸. csproj ← ۱۹. اصلاح با کامپایلر ← ۲۰. اسکن باقیمانده‌ها ← ۲۱. build ← ۲۲. گزارش

## استاندارد و نام‌گذاری abp.io

| Boilerplate | ABP.io |
|---|---|
| `X.Core` / `XCoreModule` | `X.Domain` / `XDomainModule` |
| `X.Application` | `X.Application` + `X.Application.Contracts` |
| — | `X.Domain.Shared` / `XDomainSharedModule` |
| `X.EntityFrameworkCore` / `XEntityFrameworkModule` | `X.EntityFrameworkCore` / `XEntityFrameworkCoreModule` |
| `X.Web.Core` / `XWebCoreModule` | `X.HttpApi` / `XHttpApiModule` |
| `X.Web.Host` (Startup، AuthConfigurer) | `X.HttpApi.Host` / `XHttpApiHostModule` + `Program.cs` با minimal hosting و Serilog، OpenIddict validation، Swagger OAuth، CORS |
| `X.Migrator` | `X.DbMigrator` (`XDbMigratorModule`، `DbMigratorHostedService`، `XDbMigrationService`، `IXDbSchemaMigrator`) |
| `XAppServiceBase` / `AbpServiceBase` | `XAppService : ApplicationService`، `XDomainService : DomainService`، `XAsyncBackgroundJob<TArgs>` |

کلاس‌های پایه تولیدشده اعضایی را که Boilerplate داشت (`L`، `LocalizationManager`، `SettingManager`، `CurrentUnitOfWork`، `DataFilter`...) با `LazyServiceProvider` فراهم می‌کنند تا کد سرویس‌ها و Jobها بدون تغییر گسترده کامپایل شود.

**Host:**
- `Startup` حذف و یک `XHttpApiHostModule` به سبک قالب abp.io ساخته می‌شود.
- ماژول Host قبلی (که کدهای خاص برنامه دارد: زمان‌بندی Jobهای Quartz، تنظیمات، اتصال‌ها) دور ریخته نمی‌شود. با نام `XHttpApiHostCustomModule` در build می‌ماند و ماژول جدید به آن وابسته است.
- Hubهای SignalR با همان مسیرهای قبلی (`MapHub<T>("/route")`) در `UseConfiguredEndpoints` ثبت می‌شوند.
- بقیه ثبت‌ها و middlewareهای سفارشی `Startup` به‌صورت کامنت TODO در ماژول می‌آیند، حتی اگر `Startup` قبلاً به `_BoilerplateLegacy` رفته باشد.
- سازنده ماژول‌ها که `IWebHostEnvironment`/`IConfiguration` می‌گرفتند (ABP.io ماژول را با سازنده بدون پارامتر می‌سازد) به فیلدهایی تبدیل می‌شوند که در `PreConfigureServices` مقدار می‌گیرند. فراخوانی‌های `base.PreInitialize()`/`PostInitialize()` هم حذف می‌شوند.

**سرویس‌های هر لایه:** در Boilerplate، `IHttpClientFactory` و `IHubContext<T>` را Host ثبت می‌کرد. در abp.io هر ماژول چیزی را که لایه‌اش لازم دارد ثبت می‌کند (`AddHttpClient()`، `AddSignalR()`)، تا لایه در تست‌ها و DbMigrator هم کار کند.

## Module Zero چیست و به چه تبدیل می‌شود؟

**Module Zero** در Boilerplate بسته‌ی `Abp.Zero.*` است که کاربر، نقش، Tenant، مجوزها، تنظیمات، Organization Unit، لاگین و ... را پیاده می‌کند. پروژه از کلاس‌های پایه آن ارث می‌برد (`User : AbpUser<User>`، `Role : AbpRole<User>`، `Tenant : AbpTenant<User>`، `UserManager : AbpUserManager<Role, User>`، `XDbContext : AbpZeroDbContext<...>`).

در abp.io چیزی به نام Module Zero **وجود ندارد**. همین قابلیت‌ها در چند ماژول مستقل آماده‌اند:

| Module Zero | ABP.io |
|---|---|
| User، Role، UserManager، RoleManager، OrganizationUnit | ماژول **Identity** (`IdentityUser`، `IdentityRole`، `IdentityUserManager`...) |
| Tenant، TenantManager، Edition | **TenantManagement** و **FeatureManagement** |
| PermissionChecker، UserPermissionSetting/RolePermissionSetting | **PermissionManagement** (`PermissionGrant`) |
| SettingManager، Setting | **SettingManagement** |
| LogInManager، TokenAuthController، JWT | **Account** + **OpenIddict** |

به همین دلیل کلاس‌های Module Zero پروژه حذف نمی‌شوند. ابزار آن‌ها را به این ماژول‌ها وصل می‌کند:
- ارجاع‌ها جایگزین می‌شوند: `User` به `IdentityUser` و `IRepository<User, long>` به `IRepository<IdentityUser, Guid>`.
- پراپرتی‌های سفارشی User/Role/Tenant به **Module Entity Extensions** تبدیل می‌شوند (`XModuleExtensionConfigurator` و `XEfCoreEntityExtensionMappings`)؛ دسترسی به آن‌ها با `GetProperty`/`SetProperty` است.
- ثابت‌ها به کلاس `{Name}Consts` منتقل می‌شوند.
- کلاس‌های قالبی Boilerplate (UserManager، LogInManager، SessionAppService...) به `_BoilerplateLegacy` منتقل و از build خارج می‌شوند.
- DbContext با `[ReplaceDbContext(typeof(IIdentityDbContext), typeof(ITenantManagementDbContext))]` و `ConfigureIdentity()`/`ConfigureOpenIddict()`... یکپارچه می‌شود.
- **دیتابیس** (SQL Server)، بدون از دست رفتن داده:
  1. Migration `RenameModuleZeroTables` جداول قبلی را (همراه نام PK/FK/Index) به `Zero_*` تغییر نام می‌دهد تا جداول ABP.io کنارشان ساخته شوند.
  2. Migration `ConvertModuleZeroKeys` جدول نگاشت `Zero_IdMap` (شناسه قدیم ← Guid جدید) را می‌سازد. ستون‌های **جداول خود برنامه** را هم با همین نگاشت تبدیل می‌کند:
     - کلیدهای خارجی کاربر/Tenant/نقش (`UserId bigint`، `TenantId int`، `RoleId int` ← `uniqueidentifier`)؛
     - ستون‌های Audit (`CreatorUserId` ← `CreatorId`، `LastModifierUserId` ← `LastModifierId`، `DeleterUserId` ← `DeleterId`).

     FKهای قدیمی حذف و Indexها دوباره ساخته می‌شوند. اگر شناسه‌ای نگاشت نداشته باشد، migration با پیام روشن متوقف می‌شود. snapshot هم به‌روز می‌شود، پس migration بعدی (`AbpIoModules`) فقط جداول ABP.io و FKهای جدید را اضافه می‌کند و ستون Audit را حذف/اضافه نمی‌کند.
  3. اسکریپت `ModuleZeroDataMigration.sql` کاربران، نقش‌ها، Tenantها، UserRoleها و مجوزها را با **همان Guidهای `Zero_IdMap`** به جداول ABP.io کپی می‌کند. Hash رمزها سازگار است (ASP.NET Core Identity).
- در کد هم قاعده‌ی شناسه‌ها یکسان است (`ZeroMappings.GuidIdKind`): پراپرتی‌ها، پارامترها و متغیرهای `*UserId`/`*TenantId`/`*RoleId`/`*OrganizationUnitId` به `Guid` و رشته‌های `[ForeignKey("CreatorUserId")]`/`HasForeignKey("…")` به نام‌های ABP.io تبدیل می‌شوند. پس مدل EF و دیتابیس هم‌خوان می‌مانند.

### نوع کلیدها (`--id-type`)

ماژول‌های Identity و TenantManagement در ABP.io کلید Guid دارند. فیلدهای این ماژول‌ها در هر دو حالت از همین استاندارد پیروی می‌کنند: `TenantId` و `CreatorId`/`LastModifierId`/`DeleterId`، که داده‌شان با `Zero_IdMap` تبدیل می‌شود. برای ارجاع‌های **خود برنامه** به کاربر، نقش و Organization Unit:

| | `source` (پیش‌فرض) | `guid` |
|---|---|---|
| `UserId`، `OwnerUserId`، `RoleId`… در کد و دیتابیس | همان نوع مبدا (long/int) | Guid (با تبدیل داده) |
| اتصال به `IdentityUser`/`IdentityRole` | خصوصیت و ستون `LegacyId` (شناسه قدیم؛ ردیف‌های جدید از sequence شماره می‌گیرند) و `HasPrincipalKey("LegacyId")` برای هر navigation با کلید long/int | کلید Guid |
| کاربر جاری | `CurrentUser.GetLegacyId()` / `GetRequiredLegacyId()` (claim `legacy_user_id`) | `CurrentUser.Id` |
| پیدا کردن کاربر با شناسه قدیم | `ILegacyIdLookup.FindUserAsync(long)` و `user.GetLegacyId()` | — |
| شناسه تنانت در کد و API برنامه | مثل مبدا int است: `Tenant.LegacyId` (شناسه قدیم؛ تنانت‌های جدید از sequence شماره می‌گیرند). جایی که `tenant.Id` به‌عنوان int استفاده شده، در کوئری‌های EF Core به `EF.Property<int>(t, "LegacyId")` و در بقیه جاها به `tenant.GetLegacyId()` تبدیل می‌شود. `repository.DeleteAsync(intId)` به شرطی روی `LegacyId` تبدیل می‌شود. هدر یا کوکی `Abp.TenantId` کلاینت‌های Boilerplate، تنانتی را که همان `LegacyId` را دارد انتخاب می‌کند | Guid |

تنانت‌ها: Boilerplate دو فیلد `TenancyName` (شناسه) و `Name` (نام نمایشی) دارد، ولی ABP.io فقط `Name` دارد. `Name` نام نمایشی را نگه می‌دارد (حداکثر ۶۴ کاراکتر؛ در صورت تکراری بودن، با tenancy name یکتا می‌شود). `TenancyName` و `IsActive` هم به‌صورت extension روی همان داده نگه داشته می‌شوند. پس `tenant.Name` و `tenant.TenancyName` در کد همان معنای قبلی را دارند.

Extensionهایی که داخل کوئری‌های EF Core خوانده می‌شوند (`GetProperty`، `GetLegacyId()`) به SQL ترجمه نمی‌شوند. بعد از اینکه solution کامپایل شد، این موارد با کمک semantic model پروژه‌های تبدیل‌شده (با referenceهای واقعی‌شان) به `EF.Property<T>(x, "Name")` تبدیل می‌شوند.

DTOها و مدل‌ها نوع شناسه مبدا را نگه می‌دارند (تنانت `int` و کاربر `long`) تا API تغییر نکند؛ Entityها شناسه‌های ABP.io (Guid) را دارند. هر جا این دو به هم می‌رسند (`entity.TenantId == input.TenantId`، `dto.UserId = user.Id`، `CurrentTenant.Id`…)، اصلاح‌های مبتنی بر کامپایلر `LegacyTenantIds.ToTenantGuid / ToLegacyTenantId` و `LegacyUserIds.ToUserGuid / ToLegacyUserId` را اضافه می‌کنند (در Domain.Shared؛ با cache، و از `LegacyId` در دیتابیس خوانده می‌شوند).

### API وب (`--api-style`)

همه endpointهای مبدا با همان مسیر و همان HTTP verb باقی می‌مانند تا فرانت بدون تغییر کار کند:

- Application Serviceها و Controllerها هیچ‌وقت به پوشه legacy منتقل نمی‌شوند، حتی کلاس‌های قالب (`UserAppService`، `TenantAppService`، `SessionAppService`، `TokenAuthController`…). هر بخشی که کامپایل نشود، عضو به عضو stub می‌شود: پارامتر سازنده یا فیلدی که resolve نشود حذف می‌شود، نوع ناشناخته در امضای متد به `object` تبدیل می‌شود و بدنه متد `NotImplementedException` می‌دهد. به این ترتیب مسیر وجود دارد و گزارش، کارهای باقی‌مانده را فهرست می‌کند.
- حالت `boilerplate` (پیش‌فرض) کلاس `BoilerplateApi` را در پروژه HttpApi می‌سازد:
  - جایگزینی برای `IConventionalRouteBuilder` که مسیرهای `/api/services/app/{Service}/{Action}` را با همان قواعد verb می‌سازد (`Get…` → GET، `Update…` → PUT، `Delete…` → DELETE و بقیه POST)؛
  - یک result filter که نتیجه‌ها، خطاها (`RemoteServiceErrorResponse`) و پاسخ‌های 401/403 را به شکل `{ result, targetUrl, success, error, unAuthorizedRequest, __abp }` برمی‌گرداند.

  `[DontWrapResult]` و `[WrapResult]` همچنان کار می‌کنند (نوع‌های سازگاری در Domain.Shared). endpointهای ماژول‌های ABP.io (`/api/abp/…`، `/api/identity/…`، `/connect/token`) تغییری نمی‌کنند.
- `--verify-host` endpointهای مبدا (با قواعد Boilerplate) را با `swagger.json` تبدیل‌شده مقایسه می‌کند و endpointهای گم‌شده و verbهای تغییرکرده را در بخش «Endpoints of the source» گزارش فهرست می‌کند.
- لاگین TokenAuth همچنان کار می‌کند:
  - `TokenAuthConfiguration` و پیکربندی‌اش باقی می‌مانند و به‌صورت singleton ثبت می‌شوند.
  - Host توکن JWT آن را در کنار OpenIddict می‌پذیرد (`BoilerplateJwtBearer`: توکن‌های امضاشده‌ی issuer تنظیم‌شده، و `enc_auth_token` برای SignalR).
  - سرویس‌های لاگین Boilerplate روی ABP.io ساخته می‌شوند: `LogInManager.LoginAsync` → `AbpLoginResult` (با IdentityUserManager و همان hash رمزها)، `AbpLoginResultTypeHelper`، `ITenantCache`، `IClientInfoProvider`، `SimpleStringCipher` (همان الگوریتم و پیش‌فرض‌ها) و متدهای `ISettingManager` شامل `GetSettingValueForApplication<T>` و `…ForTenant` و `…ForUser` و `ChangeSettingFor…`.
- محلی‌سازی: `LocalizationManager.GetAllSources()` و `GetSource(name)` (نام، `GetString`، `GetAllStrings(culture)`) روی منابع محلی‌سازی ABP.io.

### Viewهای SQL

Viewهایی که با SQL خام در migrationهای قبلی ساخته شده‌اند، به جداول Module Zero و ستون‌های Audit ارجاع می‌دهند. `RenameModuleZeroTables` تعریفشان را در `Zero_ViewDefinitions` ذخیره و آن‌ها را حذف می‌کند. `AbpIoModules` دوباره می‌سازدشان (`Zero_RecreateViews`): نام ستون‌های Audit به‌روز می‌شود و Viewهای وابسته دوباره امتحان می‌شوند. Viewای که باز هم ساخته نشود، با خطایش در `Zero_ViewDefinitions` می‌ماند و در گزارش می‌آید. Entityهایی که به View نگاشت شده‌اند (`ToView`) هیچ‌وقت در migration تولیدشده مثل جدول تغییر داده نمی‌شوند.

## ماژول‌های Contract (`Domain.Shared` و `Application.Contracts`)

| مقصد | چه چیزی |
|---|---|
| `X.Domain.Shared` | enumها، کلاس‌های ثابت (`PermissionNames`، `AppConsts`...)، DTOهای ساده پروژه Domain، منابع لوکالیزیشن (JSON) |
| `X.Application.Contracts` | DTOها، اینترفیس‌های Application Service، enumهای لایه Application، `PermissionDefinitionProvider` |

- واحد جابه‌جایی «یک نوع» است، نه یک فایل (namespace حفظ می‌شود).
- یک نوع فقط وقتی منتقل می‌شود که همه وابستگی‌هایش در لایه مجاز باشند؛ بقیه سر جایشان می‌مانند و دلیلش در گزارش می‌آید.
- `[AutoMap(typeof(Entity))]` روی DTOهای منتقل‌شده به `CreateMap<,>()` در یک Profile تبدیل می‌شود. `Entity.MaxNameLength` هم به `EntityConsts.MaxNameLength` تبدیل می‌شود.

## ادغام Migrationها با حفظ تاریخچه

- همه migrationها در `SquashedBoilerplateMigrations` ادغام می‌شوند. این migration **شناسه آخرین migration را حفظ می‌کند**، پس `__EFMigrationsHistory` دیتابیس‌های موجود دست نمی‌خورد.
- بدنه `Up()`/`Down()` هر migration بدون تغییر و به ترتیب اصلی در فایل‌های سالانه قرار می‌گیرد، پس دیتابیس جدید دقیقاً همان schema را می‌سازد. فقط Designer آخر نگه داشته می‌شود.
- `MIGRATION-HISTORY.md` فهرست migrationهای ادغام‌شده است. `squash-precheck.sql` قبل از استقرار بررسی می‌کند که دیتابیس به آخرین migration رسیده باشد.

## تست‌های پیش‌فرض abp.io (Volo)

| پروژه | محتوا |
|---|---|
| `X.TestBase` | `XTestBaseModule` (Autofac، مجوز همیشه مجاز، غیرفعال‌سازی Background Job، Data Seed)، `XTestBase<TModule>` با `WithUnitOfWorkAsync`، `XTestConsts`، `FakeCurrentPrincipalAccessor` |
| `X.Domain.Tests` | `XDomainTestModule`، تست‌های abstract هر `DomainService`، و `SampleDomainTests` قالب abp.io (ایجاد/تغییر کاربر با `IdentityUserManager`) |
| `X.Application.Tests` | `XApplicationTestModule`، تست‌های abstract هر Application Service، `SampleAppServiceTests` (لیست کاربران با `IIdentityUserAppService`) |
| `X.EntityFrameworkCore.Tests` | `XEntityFrameworkCoreTestModule` با SQLite in-memory، `XEntityFrameworkCoreFixture`/Collection، `SampleRepositoryTests`، تست کوئری هر `DbSet` و نسخه EF Core همه تست‌های abstract |

مدل یک پروژه واقعی معمولاً SQL مخصوص SQL Server دارد (ستون محاسباتی با `LEN`/`CAST`، `HasDefaultValueSql`، Index فیلترشده، Check Constraint) که SQLite اجرا نمی‌کند. `SqliteTestModelCustomizer` تولیدشده این‌ها را فقط از مدل دیتابیس تست حذف می‌کند و مدل اصلی دست نمی‌خورد. اگر پروژه داده مکانی (NetTopologySuite) داشته باشد، تست‌ها با `UseNetTopologySuite()` و پکیج `Microsoft.EntityFrameworkCore.Sqlite.NetTopologySuite` ساخته می‌شوند.

## اصلاح خودکار با کامپایلر (Compiler-guided fixes)

خروجی تا ۱۰ بار build می‌شود و خطاهایی که الگوی شناخته‌شده دارند با Roslyn در محل دقیق اصلاح می‌شوند، از جمله:
- اعضای `IdentityUser`/`IdentityRole`/`Tenant`: تغییر نام‌ها (مثلاً `EmailAddress` به `Email`)، پراپرتی‌های سفارشی به `GetProperty`/`SetProperty`، و `FullName` به `Name + Surname`.
- متدهای حذف‌شده Repository (sync به async، `GetAll` به `GetQueryableAsync`، `InsertRangeAsync` به `InsertManyAsync`...).
- setterهای protected: `entity.Id = x` به `EntityHelper.TrySetId` و `new E { Id = x }` به `.SetProtected(e => e.Id, x)`. setterهای کاربر به `SetPhoneNumber`/`SetIsActive`... تبدیل می‌شوند.
- اعضایی که Boilerplate از کلاس پایه می‌داد (`CurrentTenant`، `CurrentUser`، `DataFilter`، `AuthorizationService`، `CurrentUnitOfWork`...) در کلاس‌های بدون پایه به‌صورت property-injected تعریف می‌شوند.
- استفاده از `LocalizedString` به‌جای string با `.Value` اصلاح می‌شود. `long.Parse(userId)` در جایی که Guid لازم است به `Guid.Parse` و `uow.SetTenantId` به `CurrentTenant.Change` تبدیل می‌شود.
- `[UnitOfWork(isolationLevel)]` به سازنده ABP.io، override‌های `AsyncCrudAppService` (`UpdateAsync(input)`، `DeleteAsync(EntityDto)`) به امضای `CrudAppService` (`UpdateAsync(id, input)`، `DeleteAsync(id)`)، `IRepository<BackgroundJobInfo, long>` به `IRepository<BackgroundJobRecord, Guid>`، `UserFriendlyException(msg, ex)` به `innerException: ex`، `SimpleStringCipher.DefaultPassPhrase` به `AbpStringEncryptionOptions` و حذف `using`های namespaceهای ناموجود.
- NU1605 (downgrade پکیج) با به‌روزرسانی نسخه رفع می‌شود.

با `--stub-unresolved`، اگر اصلاح خودکار متوقف شود، عضوهایی که هنوز خطا دارند (متد، سازنده، accessor) بدنه تبدیل‌شده‌شان را زیر `#if ABPTOVOLO_ORIGINAL` نگه می‌دارند و `NotImplementedException` پرتاب می‌کنند. اگر خطا در تعریف باشد (نوع فیلد، کلاس پایه)، کل فایل زیر `#if` می‌رود؛ به‌جز ماژول‌ها و DbContext. بعد build ادامه پیدا می‌کند تا پروژه‌های بعدی (Application، HttpApi، Host، تست‌ها) هم بررسی و اصلاح شوند. هیچ کدی حذف نمی‌شود و فهرست کامل در بخش «Members kept under #if ABPTOVOLO_ORIGINAL» گزارش می‌آید.

## Shimهای سازگاری

فقط وقتی کد از این APIها استفاده کند، در پروژه Domain (پوشه `AbpToVoloCompat`) ساخته می‌شوند و در گزارش ذکر می‌شوند تا به‌تدریج حذف شوند:
`ObjectMapper.Map<TDest>(src)`، `JobBase` (Quartz)، `IExtendableObject`، `ILocalizationManager.GetString(source, key)`، `ICacheManager`/`ITypedCache` (روی `IDistributedCache`، با `[ExposeServices]` تا در DI ثبت شود)، `SetProtected`.

Repositoryهای سفارشی که ADO.NET خام یا Stored Procedure اجرا می‌کنند و از `IActiveTransactionProvider` استفاده می‌کردند، به روش ABP.io تبدیل می‌شوند: `(await GetDbContextAsync()).Database.CurrentTransaction?.GetDbTransaction()` و `Database.GetDbConnection()`. پارامتر و فیلد آن هم از سازنده‌ها حذف می‌شود. Shim `IActiveTransactionProvider` فقط وقتی ساخته می‌شود که استفاده‌ای باقی مانده باشد.

`IRepository<IdentityUserRole>` و موجودیت‌های مشابه Identity که Aggregate Root نیستند، با یک `DbSet` در DbContext برنامه Repository پیش‌فرض می‌گیرند.

## چه چیزهایی دستی است (در گزارش می‌آید)

- شناسه‌های عددی ثابت کاربر/Tenant در کد (مثلاً `tenantId = 1`)؛ در ABP.io باید Guid باشند (از `Zero_IdMap`).
- navigationهای Entityهای Module Zero (مثلاً `User.Customer`)؛ ماژول Identity اجازه navigation روی `IdentityUser` نمی‌دهد و باید با Repository یا کوئری جدا خوانده شوند.
- کد سفارشی ساخت کاربر با `new User { ... }` و setterها؛ باید به `new IdentityUser(id, userName, email, tenantId)` + `IdentityUserManager.CreateAsync` تبدیل شود.
- متدهای سفارشی کلاس‌های Module Zero پروژه (مثلاً `UserManager.GetCustomerIdFromUserId`)؛ `UserManager` جای خود را به `IdentityUserManager` می‌دهد و این متدها باید به یک Domain Service جدا منتقل شوند. کد قبلی در `_BoilerplateLegacy` موجود است.
- `AuditLog`/`EntityChange` (ماژول AuditLogging در ABP.io)، `IAbpAntiForgeryManager`، `IClientInfoProvider`، `IErrorInfoBuilder` و Exception Handlerهای اختصاصی Host.
- `EmailConfirmationCode`/`PasswordResetCode` (در ABP.io توکن‌های Identity)، `SimpleStringCipher` (`IStringEncryptionService`).
- Frontend (Angular/Vue/MVC) تبدیل نمی‌شود.

## ساختار

```
src/AbpToVolo.Engine/   موتور (کتابخانه)
  Core/                   گزینه‌ها، تنظیمات، context، observer و لاگ، گزارش، build
  Mappings/               جدول نگاشت namespace / پکیج / ماژول / نوع / Module Zero
  CodeMigration/          rewriterهای Roslyn (هر کدام یک دسته تبدیل)
  Steps/                  ۲۲ مرحله تبدیل
src/AbpToVolo/          خط فرمان (dotnet tool: abp-to-volo)
src/AbpToVolo.Desktop/  برنامه ویندوزی WinForms
tests/AbpToVolo.Tests/  تست واحد rewriterها + تست end-to-end روی نمونه
samples/Acme.PhoneBook/  solution نمونه Boilerplate (شامل Module Zero)
```

برای افزودن یک تبدیل جدید: یک کلاس از `MigrationRewriter` بسازید و در `CSharpFileMigrator.Pipeline` اضافه کنید، یا جدول‌های `Mappings/` را گسترش دهید. یک مرحله جدید هم با پیاده‌سازی `IMigrationStep` و افزودن به `MigrationEngine` اضافه می‌شود.

## اعتبارسنجی

```bash
dotnet test
dotnet run --project src/AbpToVolo -- samples/Acme.PhoneBook -o ../out/PhoneBook --force --build
```

- تست‌های ابزار: ۹۰ تست پاس می‌شوند. یکی از آن‌ها بررسی می‌کند که تبدیل دوباره‌ی همان سورس دقیقاً همان خروجی را بدهد.
- نمونه `Acme.PhoneBook` (با Module Zero) بعد از تبدیل با ABP 10.7.0 بدون خطا build می‌شود و همه ۲۰ تست تولیدشده، از جمله تست‌های نمونه abp.io، پاس می‌شوند. تنها هشدار NU1903 است: AutoMapper 14 که `Volo.Abp.AutoMapper` استفاده می‌کند آسیب‌پذیری گزارش‌شده دارد. پیش‌فرض ABP 10 کتابخانه Mapperly است و مهاجرت به آن توصیه می‌شود.
- ابزار روی یک solution واقعی و بزرگ Boilerplate (چند هزار فایل C#، صدها migration، Module Zero، SignalR، Quartz و Repositoryهای سفارشی) هم آزمایش شده است. با `--stub-unresolved` کل solution تبدیل‌شده بدون خطا build می‌شود و بیشتر تست‌های تولیدشده پاس می‌شوند. باقیمانده‌ها همان موارد بخش «چه چیزهایی دستی است» هستند.
