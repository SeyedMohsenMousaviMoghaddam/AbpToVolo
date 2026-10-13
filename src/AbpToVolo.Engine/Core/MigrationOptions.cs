namespace AbpToVolo.Core;

/// <summary>Key types of the application's references to Module Zero users / roles / organization units.</summary>
public enum IdTypeMode
{
    /// <summary>Keep the source types (long/int), linked to the ABP.io entities through their LegacyId.</summary>
    Source,

    /// <summary>Convert them to Guid, the key type of the ABP.io entities (data converted through Zero_IdMap).</summary>
    Guid
}

public enum MigrationsMode
{
    /// <summary>Merge all migrations into one that keeps the id of the last one (existing databases stay valid).</summary>
    Squash,

    /// <summary>Delete the old migrations (start with a fresh "Initial" migration).</summary>
    Delete,

    /// <summary>Leave the migrations untouched.</summary>
    Keep
}

public sealed class MigrationOptions
{
    /// <summary>Root folder of the ASP.NET Boilerplate solution (never modified).</summary>
    public required string InputPath { get; init; }

    /// <summary>Folder that receives the converted ABP.io solution.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Volo.Abp.* package version written into the project files.</summary>
    public string AbpVersion { get; init; } = "10.7.0";

    /// <summary>Target framework for every converted project.</summary>
    public string TargetFramework { get; init; } = "net10.0";

    /// <summary>Solution name (e.g. Acme.PhoneBook). Detected from the .sln file when null.</summary>
    public string? SolutionName { get; init; }

    /// <summary>
    /// Use the ABP.io layer and module names (Core → Domain, Web.Core → HttpApi, Web.Host → HttpApi.Host,
    /// Migrator → DbMigrator; XCoreModule → XDomainModule ...). On by default: the result follows the ABP.io template.
    /// </summary>
    public bool RenameLayers { get; init; } = true;

    /// <summary>
    /// Replace Module Zero with the ABP.io modules: User/Role/Tenant → IdentityUser/IdentityRole/Tenant (custom properties
    /// become module entity extensions), Zero managers → ABP managers, Zero infrastructure moved out of the build,
    /// Identity/TenantManagement/Account/PermissionManagement/... modules and OpenIddict wired in.
    /// </summary>
    public bool ConvertModuleZero { get; init; } = true;

    /// <summary>Generate the ABP.io HttpApi.Host (module + Program.cs) and DbMigrator, moving Startup.cs to the legacy folder.</summary>
    public bool StandardizeHost { get; init; } = true;

    /// <summary>What happens to the existing EF Core migrations (default: merge them into one, keeping the history).</summary>
    public MigrationsMode Migrations { get; init; } = MigrationsMode.Squash;

    /// <summary>Create *.Domain.Shared and *.Application.Contracts projects and move enums/consts/DTOs/interfaces there.</summary>
    public bool CreateContracts { get; init; } = true;

    /// <summary>Generate ABP.io-template test projects (TestBase, Domain.Tests, Application.Tests, EntityFrameworkCore.Tests).</summary>
    public bool CreateTests { get; init; } = true;

    /// <summary>Build the converted solution and fix errors automatically using the compiler's diagnostics (iterative).</summary>
    public bool CompilerFixes { get; init; } = true;

    /// <summary>
    /// After the compiler-guided fixes, keep the original body of members that still do not compile under
    /// "#if ABPTOVOLO_ORIGINAL" and throw NotImplementedException, so the solution builds (each one is a report item).
    /// </summary>
    public bool StubUnresolved { get; init; }

    /// <summary>Run "dotnet build" on the converted solution and add the errors to the report.</summary>
    public bool Build { get; init; }

    /// <summary>
    /// Key types of the application's references to users, roles and organization units (UserId, RoleId...).
    /// Source (default): they keep the source type (long/int) and point to the ABP.io entities through LegacyId.
    /// Guid: they become Guid like the ABP.io keys. The ABP.io module fields (TenantId, CreatorId...) are Guid in both.
    /// </summary>
    public IdTypeMode IdType { get; init; } = IdTypeMode.Source;

    /// <summary>ConnectionStrings:Default of the converted host and DbMigrator (null = the one of the source).</summary>
    public string? ConnectionString { get; init; }

    /// <summary>Run the converted DbMigrator after the conversion (migrates and seeds the configured database).</summary>
    public bool MigrateDatabase { get; init; }

    /// <summary>Start the converted host after the conversion and check that Swagger answers (then stop it).</summary>
    public bool VerifyHost { get; init; }

    /// <summary>Delete the output folder first if it already exists.</summary>
    public bool Force { get; init; }

    public bool Verbose { get; init; }

    /// <summary>
    /// Version for Microsoft.EntityFrameworkCore.*, Microsoft.AspNetCore.* and Microsoft.Extensions.* packages.
    /// Null = read it from the ABP package dependencies on nuget.org.
    /// </summary>
    public string? MicrosoftPackagesVersion { get; init; }

    /// <summary>Floating fallback that follows the target framework: net10.0 → 10.0.*</summary>
    public string DefaultMicrosoftPackagesVersion
    {
        get
        {
            var digits = TargetFramework.Replace("net", "", StringComparison.OrdinalIgnoreCase);
            return Version.TryParse(digits, out var v) ? $"{v.Major}.{v.Minor}.*" : "10.0.*";
        }
    }
}
