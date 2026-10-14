using System.Text;
using AbpToVolo.Core;
using AbpToVolo.Steps;

namespace AbpToVolo;

public static class Program
{
    private const string Usage = """
        abp-to-volo — ASP.NET Boilerplate → ABP Framework (abp.io, Volo.Abp) converter

        Usage:
          abp-to-volo <source-folder> -o <destination-folder> [options]
          abp-to-volo --config <settings.json> [options]

        Options:
          -o, --output <folder>      Destination folder for the converted solution
          --config <file>            Read the settings (source, destination, options) from a JSON file
          --save-config <file>       Write the effective settings to a JSON file (and exit when no source is given)
          --abp-version <version>    Volo.Abp.* package version         (default: 10.7.0)
          --framework <tfm>          Target framework                    (default: net10.0)
          --ms-version <version>     Microsoft.EntityFrameworkCore/Extensions version (default: read from ABP's nuspec)
          --solution-name <name>     Solution name, e.g. Acme.PhoneBook  (default: from the .sln file)
          --keep-layer-names         Keep the Boilerplate project/module names (default: ABP.io names: Core→Domain,
                                     Web.Core→HttpApi, Web.Host→HttpApi.Host, Migrator→DbMigrator, XCoreModule→XDomainModule)
          --no-zero                  Do not replace Module Zero with the ABP.io Identity/TenantManagement/... modules
          --no-host                  Do not generate the ABP.io HttpApi.Host module/Program.cs and DbMigrator
          --migrations <mode>        squash (default): merge all migrations into one, keeping the history
                                     delete: remove them; keep: leave them untouched
          --no-contracts             Do not create *.Domain.Shared / *.Application.Contracts
          --no-tests                 Do not generate the ABP.io test projects
          --no-compiler-fixes        Do not build the result to fix errors from the compiler diagnostics (default: on)
          --stub-unresolved          Errors the fixes cannot solve: keep the original body under #if ABPTOVOLO_ORIGINAL and
                                     throw NotImplementedException, so the solution builds (each one is listed in the report)
          --build                    Run 'dotnet build' on the result and add the remaining errors to the report
          --id-type <type>           source (default): the application's user/role/organization unit references keep the
                                     source key type (long/int), linked to the ABP.io entities through LegacyId;
                                     guid: convert them to Guid. TenantId and the audit fields are Guid in both (ABP.io standard)
          --api-style <style>        boilerplate (default): same HTTP API as the source (/api/services/app/{Service}/{Method},
                                     AjaxResponse envelope), so the existing front end keeps working;
                                     abp: ABP.io conventions (/api/app/{service}, results not wrapped)
          --connection-string <cs>   ConnectionStrings:Default of the converted host and DbMigrator (default: the source's;
                                     or set ABPTOVOLO_CONNECTION_STRING). Not saved with --save-config.
          --migrate-database         Run the converted DbMigrator on that database (CHANGES THE DATABASE: use a copy)
          --verify-host              Start the converted host and check that Swagger answers, then stop it
          --force                    Replace the destination folder if it is not empty
          -v, --verbose              Show every change in the console (the log file always has everything)
          -h, --help                 Show this help

        The source folder is never modified. The destination gets ABP-MIGRATION-REPORT.md and abp-migration.log.
        Press Ctrl+C to cancel.
        """;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        MigrationSettings settings;
        try
        {
            var parsed = ParseSettings(args, out var saveConfig);
            if (parsed is null)
            {
                Console.WriteLine(Usage);
                return 0;
            }

            settings = parsed;
            if (saveConfig is not null)
            {
                settings.Save(saveConfig);
                Console.WriteLine($"Settings saved to {Path.GetFullPath(saveConfig)}");
                if (string.IsNullOrWhiteSpace(settings.SourcePath)) return 0;
            }

            var errors = settings.Validate();
            if (errors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine + "       ", errors));
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        var fileLog = new FileLogObserver();
        var observer = new CompositeObserver(new ConsoleObserver(settings.Verbose), fileLog);
        var options = settings.ToOptions();
        try
        {
            new MigrationEngine().Run(options, observer, cancellation.Token);
            Console.WriteLine($"Report: {Path.Combine(options.OutputPath, ReportWriterStep.MarkdownFile)}");
            Console.WriteLine($"Log:    {fileLog.LogFilePath}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 3;
        }
        catch (MigrationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex}");
            Console.Error.WriteLine($"Log: {fileLog.LogFilePath}");
            return 1;
        }
    }

    internal static MigrationOptions? ParseArguments(string[] args) => ParseSettings(args, out _) is { } settings
        ? Check(settings).ToOptions()
        : null;

    private static MigrationSettings Check(MigrationSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SourcePath)) throw new ArgumentException("The source folder is required.");
        if (string.IsNullOrWhiteSpace(settings.DestinationPath)) throw new ArgumentException("The destination folder (-o) is required.");
        return settings;
    }

    internal static MigrationSettings? ParseSettings(string[] args, out string? saveConfig)
    {
        saveConfig = null;
        if (args.Length == 0 || args.Any(a => a is "-h" or "--help" or "/?"))
        {
            return null;
        }

        // --config is applied first, the other options override it.
        var configIndex = Array.IndexOf(args, "--config");
        var settings = configIndex >= 0 && configIndex + 1 < args.Length ? MigrationSettings.Load(args[configIndex + 1]) : new MigrationSettings();
        string? source = null;

        for (var i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {args[i]}");

            switch (args[i])
            {
                case "--config": i++; break;
                case "--save-config": saveConfig = Value(); break;
                case "-o" or "--output": settings.DestinationPath = Value(); break;
                case "--abp-version": settings.AbpVersion = Value(); break;
                case "--framework": settings.TargetFramework = Value(); break;
                case "--ms-version": settings.MicrosoftPackagesVersion = Value(); break;
                case "--solution-name": settings.SolutionName = Value(); break;
                case "--rename-layers": settings.UseAbpLayerNames = true; break;
                case "--keep-layer-names": settings.UseAbpLayerNames = false; break;
                case "--no-zero": settings.ConvertModuleZero = false; break;
                case "--no-host": settings.StandardizeHost = false; break;
                case "--keep-migrations": settings.Migrations = MigrationsMode.Keep; break;
                case "--migrations":
                    var mode = Value();
                    settings.Migrations = Enum.TryParse<MigrationsMode>(mode, ignoreCase: true, out var parsedMode)
                        ? parsedMode
                        : throw new ArgumentException($"Unknown migrations mode '{mode}' (squash, delete, keep)");
                    break;
                case "--no-contracts": settings.CreateContracts = false; break;
                case "--no-tests": settings.CreateTests = false; break;
                case "--build": settings.Build = true; break;
                case "--no-compiler-fixes": settings.CompilerFixes = false; break;
                case "--stub-unresolved": settings.StubUnresolved = true; break;
                case "--connection-string": settings.ConnectionString = Value(); break;
                case "--id-type":
                    var idType = Value();
                    settings.IdType = Enum.TryParse<IdTypeMode>(idType, ignoreCase: true, out var parsedIdType)
                        ? parsedIdType
                        : throw new ArgumentException($"Unknown id type '{idType}' (source, guid)");
                    break;
                case "--api-style":
                    var apiStyle = Value();
                    settings.ApiStyle = Enum.TryParse<ApiStyleMode>(apiStyle, ignoreCase: true, out var parsedApiStyle)
                        ? parsedApiStyle
                        : throw new ArgumentException($"Unknown API style '{apiStyle}' (boilerplate, abp)");
                    break;
                case "--migrate-database": settings.MigrateDatabase = true; break;
                case "--verify-host": settings.VerifyHost = true; break;
                case "--force": settings.Force = true; break;
                case "-v" or "--verbose": settings.Verbose = true; break;
                case var a when a.StartsWith('-'): throw new ArgumentException($"Unknown option {a}");
                default:
                    if (source is not null) throw new ArgumentException($"Unexpected argument {args[i]}");
                    source = args[i];
                    break;
            }
        }

        if (source is not null) settings.SourcePath = source;
        return settings;
    }
}
