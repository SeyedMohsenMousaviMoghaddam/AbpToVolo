using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using AbpToVolo.CodeMigration;
using AbpToVolo.Core;
using AbpToVolo.Mappings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AbpToVolo.Steps;

/// <summary>
/// Replaces the Boilerplate Startup-based host and the Migrator with the ABP.io template equivalents:
///   HttpApi.Host: {Prefix}HttpApiHostModule (OpenIddict validation, Swagger + OAuth, CORS, standard middleware pipeline)
///                 and a minimal-hosting Program.cs (Autofac, Serilog). Startup.cs/Program.cs/the old module move to the
///                 legacy folder; their custom service registrations and middlewares are listed in the new module.
///   Domain:       OpenIddict data seeder (scope + SPA/Swagger clients from appsettings), I{Prefix}DbSchemaMigrator,
///                 {Prefix}DbMigrationService.
///   EF Core:      EntityFrameworkCore{Prefix}DbSchemaMigrator.
///   DbMigrator:   Program.cs, DbMigratorHostedService, {Prefix}DbMigratorModule (created when the solution had no Migrator).
/// </summary>
public sealed class HostStandardizationStep : IMigrationStep
{
    private static readonly (string Name, string Version)[] SerilogHostPackages = [("Serilog.AspNetCore", "9.0.0"), ("Serilog.Sinks.Async", "2.1.0")];

    private static readonly (string Name, string Version)[] SerilogConsolePackages =
        [("Serilog.Extensions.Logging", "9.0.0"), ("Serilog.Sinks.Async", "2.1.0"), ("Serilog.Sinks.File", "6.0.0"), ("Serilog.Sinks.Console", "6.0.0")];

    private static readonly HashSet<string> StartupClasses = new(StringComparer.Ordinal) { "Startup", "Program", "AuthConfigurer" };

    public string Name => "ABP.io host and DbMigrator";

    public bool IsEnabled(MigrationOptions options) => options.StandardizeHost;

    private sealed record Modules(string? HttpApi, string? Application, string? Contracts, string? EfCore, string? DbContext)
    {
        /// <summary>The old host module kept as {Prefix}HttpApiHostCustomModule (fully qualified), if any.</summary>
        public string? HostCustom { get; init; }

        public bool SignalR { get; init; }

        /// <summary>endpoints.MapHub&lt;THub&gt;("/route") of the old Startup: hub type (fully qualified) and argument list.</summary>
        public List<(string Hub, string Arguments)> Hubs { get; init; } = [];

        /// <summary>ABP.io's SignalR module is used: it maps every hub itself, routes are set with AbpSignalROptions.</summary>
        public bool AbpSignalR { get; init; }
    }

    public void Execute(MigrationContext context)
    {
        var domain = context.FindProject(ProjectLayer.Domain);
        var efCore = context.FindProject(ProjectLayer.EntityFrameworkCore);
        if (domain is null || efCore is null)
        {
            context.Report.Warning(Name, "Domain or EntityFrameworkCore project not found; host and DbMigrator were not generated.");
            return;
        }

        var prefix = new string(context.ModulePrefix.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        var modules = new Modules(
            ModuleName(context, ProjectLayer.HttpApi),
            ModuleName(context, ProjectLayer.Application),
            ModuleName(context, ProjectLayer.ApplicationContracts),
            ModuleName(context, ProjectLayer.EntityFrameworkCore),
            DbContextName(efCore));

        var host = context.FindProject(ProjectLayer.Host);
        var settings = host is null ? null : ReadSettings(Path.Combine(host.Directory, "appsettings.json"));

        if (host is not null)
        {
            StandardizeHost(context, host, prefix, modules, settings!);
        }

        if (context.UsesIdentityModules)
        {
            WriteOpenIddictSeeder(context, domain, prefix);
        }

        if (modules.DbContext is not null)
        {
            WriteDbMigration(context, domain, efCore, prefix, modules, settings);
        }

        context.RefreshProjects();
    }

    // ---------- HttpApi.Host ----------

    private void StandardizeHost(MigrationContext context, ProjectInfo host, string prefix, Modules modules, JsonObject settings)
    {
        var carried = new List<string>();
        var moved = new List<string>();
        var hubs = new List<(string Hub, string Arguments)>();
        var signalR = false;
        string? customModule = null;
        var legacyFolder = Path.DirectorySeparatorChar + FileSystem.LegacyFolder + Path.DirectorySeparatorChar;
        var legacyRoot = Path.Combine(host.Directory, FileSystem.LegacyFolder);
        var legacyFiles = Directory.Exists(legacyRoot) ? Directory.EnumerateFiles(legacyRoot, "*.cs", SearchOption.AllDirectories) : [];
        foreach (var file in FileSystem.EnumerateFiles(host.Directory, "*.cs").Concat(legacyFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            // Startup may already be in the legacy folder (moved by the Module Zero step because it uses IdentityRegistrar...):
            // its custom registrations are carried anyway.
            var inLegacy = file.Contains(legacyFolder, StringComparison.OrdinalIgnoreCase);
            if (context.IsExcluded(file) && !inLegacy) continue;

            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetCompilationUnitRoot();
            var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
            var isStartup = classes.Any(c => StartupClasses.Contains(c.Identifier.Text));
            var isModule = classes.Any(c => SyntaxHelpers.HasBaseType(c, "AbpModule"));
            if (!isStartup && !isModule) continue;

            if (classes.FirstOrDefault(c => c.Identifier.Text == "Startup") is { } startup)
            {
                carried.AddRange(CustomStatements(startup));
                signalR |= startup.ToString().Contains("AddSignalR", StringComparison.Ordinal);
                hubs.AddRange(MapHubCalls(context, startup));
            }

            if (inLegacy) continue;

            // The old host module holds application configuration (background workers, settings...): it stays compiled
            // as {Prefix}HttpApiHostCustomModule and the new host module depends on it.
            if (isModule && !isStartup && customModule is null && KeepAsCustomModule(context, file, prefix) is { } kept)
            {
                customModule = kept;
                continue;
            }

            MoveToLegacy(host, file);
            moved.Add(Path.GetFileName(file));
        }

        if (customModule is not null) modules = modules with { HostCustom = customModule };
        if (signalR || hubs.Count > 0)
        {
            // ABP.io's SignalR module maps every hub itself (route /signalr-hubs/{name}): mapping them again fails.
            var abpSignalR = UsesAbpModule(context, "AbpAspNetCoreSignalRModule", "Volo.Abp.AspNetCore.SignalR", "Abp.AspNetCore.SignalR");
            modules = modules with { SignalR = true, Hubs = hubs, AbpSignalR = abpSignalR };
        }

        if (moved.Count > 0)
        {
            ModuleZeroStep.ExcludeLegacyFolder(host.CsprojPath);
            context.Report.Change(Name, $"Moved to {FileSystem.LegacyFolder}: {string.Join(", ", moved)} (replaced by the ABP.io host module + Program.cs)", context.Relative(host.CsprojPath));
        }

        var ns = host.RootNamespace;
        var moduleClass = $"{prefix}HttpApiHostModule";
        var scope = prefix;
        FileSystem.WriteText(Path.Combine(host.Directory, moduleClass + ".cs"), HostModule(ns, moduleClass, scope, modules, context.UsesIdentityModules, carried));
        FileSystem.WriteText(Path.Combine(host.Directory, "Program.cs"), HostProgram(ns, moduleClass));

        // ABP.io template: the client libraries of the MVC UI are restored with "abp install-libs" from this package.json.
        var packageJson = Path.Combine(host.Directory, "package.json");
        if (!File.Exists(packageJson))
        {
            var name = new string(prefix.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
            FileSystem.WriteText(packageJson, $$"""
                {
                  "version": "1.0.0",
                  "name": "{{name}}-host",
                  "private": true,
                  "dependencies": {
                    "@abp/aspnetcore.mvc.ui.theme.basic": "~{{context.Options.AbpVersion}}"
                  }
                }

                """);
        }

        context.Report.Todo(Name, "Swagger and the API work at once; for the login/account pages run 'abp install-libs' in the host folder (needs Node.js), " +
                                  "then remove the AbpMvcLibsOptions.CheckLibs = false line of the host module.", context.Relative(host.CsprojPath));

        foreach (var (package, version) in SerilogHostPackages)
        {
            ProjectWriter.AddPackageReference(host.CsprojPath, package, version);
        }

        var hostModules = AbpModuleCatalog.HostModules.Concat(context.UsesIdentityModules ? AbpModuleCatalog.HostIdentityModules : []);
        foreach (var module in hostModules)
        {
            ProjectWriter.AddPackageReference(host.CsprojPath, module.Package, context.Options.AbpVersion);
        }

        context.State.StringEncryptionPassPhrase ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        MergeHostSettings(settings, scope, context.UsesIdentityModules, context.State.StringEncryptionPassPhrase);
        if (UsesAbpModule(context, "AbpCachingStackExchangeRedisModule", "Volo.Abp.Caching.StackExchangeRedis", "Abp.RedisCache"))
        {
            MergeRedisSettings(context, host, settings);
        }

        if (context.Options.ConnectionString is { } connectionString)
        {
            Section(settings, "ConnectionStrings")["Default"] = connectionString;
            context.Report.Change(Name, "ConnectionStrings:Default set from --connection-string", context.Relative(host.CsprojPath));
        }

        WriteSettings(Path.Combine(host.Directory, "appsettings.json"), settings);

        context.Report.Change(Name, $"Generated {moduleClass} and Program.cs (ABP.io HttpApi.Host template)", context.Relative(host.CsprojPath));
        if (carried.Count > 0)
        {
            context.Report.Todo(Name, $"{carried.Count} custom service registrations/middlewares of the old Startup are listed (commented) in {moduleClass}; " +
                                      "port the ones you need into ConfigureServices / OnApplicationInitialization.", context.Relative(Path.Combine(host.Directory, moduleClass + ".cs")));
        }

        if (context.UsesIdentityModules)
        {
            context.Report.Todo(Name, "The host now authenticates with OpenIddict (/connect/token, /connect/authorize) instead of TokenAuthController/JWT: " +
                                      "update the client login flow; run 'abp install-libs' for the login pages; configure a signing certificate for production " +
                                      "(PreConfigure<AbpOpenIddictAspNetCoreOptions>(o => o.AddDevelopmentEncryptionAndSigningCertificate = false)).", context.Relative(host.CsprojPath));
        }
    }

    /// <summary>Statements of the old Startup that are not Boilerplate/ASP.NET defaults covered by the ABP.io host.</summary>
    private static IEnumerable<string> CustomStatements(ClassDeclarationSyntax startup)
    {
        string[] known =
        [
            "AddAbp", "AddApplication", "UseAbp", "InitializeApplication", "AddMvc", "AddControllers", "AddControllersWithViews", "AddCors", "UseCors",
            "AddSwaggerGen", "UseSwagger", "UseSwaggerUI", "IdentityRegistrar", "AuthConfigurer", "UseAuthentication", "UseAuthorization", "UseRouting",
            "UseEndpoints", "UseStaticFiles", "UseDeveloperExceptionPage", "UseAbpRequestLocalization", "UseJwtTokenMiddleware",
            "MapHub", "UseCastleWindsor", "UseUnitOfWork", "AddAbpWithoutCreatingServiceProvider", "LoggingFacility", "UseExceptionHandler"
        ];

        // expression statements, variable declarations (var signalRBuilder = services.AddSignalR(...)) and ifs of the method bodies
        foreach (var statement in startup.DescendantNodes().OfType<StatementSyntax>()
                     .Where(s => s is ExpressionStatementSyntax or LocalDeclarationStatementSyntax or IfStatementSyntax &&
                                 s.Parent is BlockSyntax { Parent: MethodDeclarationSyntax }))
        {
            var text = SyntaxHelpers.Text(statement);
            if (known.Any(k => text.Contains(k, StringComparison.Ordinal))) continue;
            yield return text;
        }
    }

    private static string HostModule(string ns, string moduleClass, string scope, Modules modules, bool identity, List<string> carried)
    {
        var dependsOn = new[] { modules.HttpApi, modules.Application, modules.EfCore, modules.HostCustom }.OfType<string>()
            .Concat(AbpModuleCatalog.HostModules.Select(m => m.Module))
            .Concat(identity ? AbpModuleCatalog.HostIdentityModules.Select(m => m.Module) : []);
        var usings = new SortedSet<string>(StringComparer.Ordinal)
        {
            "System", "System.Collections.Generic", "System.Linq", "Microsoft.AspNetCore.Builder", "Microsoft.AspNetCore.Cors", "Microsoft.Extensions.Configuration",
            "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting", "Volo.Abp", "Volo.Abp.Modularity", "Volo.Abp.UI.Navigation.Urls"
        };
        foreach (var module in AbpModuleCatalog.HostModules.Concat(identity ? AbpModuleCatalog.HostIdentityModules : [])) usings.Add(module.Namespace);
        if (identity)
        {
            usings.Add("OpenIddict.Validation.AspNetCore");
            usings.Add("Microsoft.AspNetCore.Extensions.DependencyInjection");
            usings.Add("Volo.Abp.Security.Claims");
        }

        var nl = Environment.NewLine;
        var hubs = modules.Hubs.Distinct().ToList();
        string signalRBlock;
        if (!modules.SignalR)
        {
            signalRBlock = "";
        }
        else if (modules.AbpSignalR)
        {
            // AbpAspNetCoreSignalRModule registers SignalR and maps the hubs: only their original routes are configured.
            usings.Add("Volo.Abp.AspNetCore.SignalR");
            signalRBlock = $"{nl}        // SignalR hubs keep the routes of the old Startup (ABP.io's SignalR module maps them).{nl}" +
                           $"        Configure<AbpSignalROptions>(options =>{nl}        {{{nl}" +
                           string.Concat(hubs.Select(h => $"            options.Hubs.AddOrUpdate(typeof({h.Hub}), config => config.RoutePattern = {HubRoute(h.Arguments)});{nl}")) +
                           $"        }});{nl}";
        }
        else
        {
            signalRBlock = $"{nl}        // SignalR hubs of the old Startup (their routes are mapped in OnApplicationInitialization){nl}        context.Services.AddSignalR();{nl}";
        }

        // The MVC UI (Basic theme, Account login pages) needs the client libraries ("abp install-libs", npm); without them ABP
        // blocks every page, Swagger included. The API works without them, so the check is off until they are installed.
        usings.Add("Volo.Abp.AspNetCore.Mvc.Libs");
        var libsBlock = $"{nl}        // Run \"abp install-libs\" in this folder for the login pages (Account module), then remove this line.{nl}" +
                        $"        Configure<AbpMvcLibsOptions>(options => options.CheckLibs = false);{nl}";

        var carriedBlock = libsBlock + signalRBlock +
                           (carried.Count == 0
                               ? ""
                               : nl + "        // TODO [AbpToVolo]: custom registrations of the old Startup.ConfigureServices/Configure (port the ones you need):" + nl +
                                 string.Concat(carried.SelectMany(c => c.Split('\n')).Select(l => $"        // {l.TrimEnd('\r')}{nl}")));

        var endpoints = hubs.Count == 0 || modules.AbpSignalR
            ? "app.UseConfiguredEndpoints();"
            : $"app.UseConfiguredEndpoints(endpoints =>{nl}        {{{nl}" + string.Concat(hubs.Select(h => $"            endpoints.MapHub<{h.Hub}>{h.Arguments};{nl}")) + "        });";

        var swagger = identity
            ? $$"""
                context.Services.AddAbpSwaggerGenWithOAuth(
                            configuration["AuthServer:Authority"]!,
                            new Dictionary<string, string> { { ApiScope, "{{scope}} API" } },
                            options =>
                            {
                                options.SwaggerDoc("v1", new() { Title = "{{scope}} API", Version = "v1" });
                                options.DocInclusionPredicate((docName, description) => true);
                                options.CustomSchemaIds(type => type.FullName);
                            });
                """
            : $$"""
                context.Services.AddAbpSwaggerGen(options =>
                        {
                            options.SwaggerDoc("v1", new() { Title = "{{scope}} API", Version = "v1" });
                            options.DocInclusionPredicate((docName, description) => true);
                            options.CustomSchemaIds(type => type.FullName);
                        });
                """;

        return $$"""
            {{string.Concat(usings.Select(u => $"using {u};{Environment.NewLine}"))}}
            namespace {{ns}};

            /* ABP.io HttpApi.Host module (generated by AbpToVolo from the ABP.io application template). */
            [DependsOn(
            {{string.Join("," + Environment.NewLine, dependsOn.Select(d => $"    typeof({d})"))}})]
            public class {{moduleClass}} : AbpModule
            {
                private const string ApiScope = "{{scope}}";
            {{(identity ? """

                public override void PreConfigureServices(ServiceConfigurationContext context)
                {
                    PreConfigure<OpenIddictBuilder>(builder =>
                    {
                        builder.AddValidation(options =>
                        {
                            options.AddAudiences(ApiScope);
                            options.UseLocalServer();
                            options.UseAspNetCore();
                        });
                    });
                }
            """ : "")}}

                public override void ConfigureServices(ServiceConfigurationContext context)
                {
                    var configuration = context.Services.GetConfiguration();
            {{(identity ? """

                    context.Services.ForwardIdentityAuthenticationForBearer(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                    context.Services.Configure<AbpClaimsPrincipalFactoryOptions>(options =>
                    {
                        options.IsDynamicClaimsEnabled = true;
                    });
            """ : "")}}

                    Configure<AppUrlOptions>(options =>
                    {
                        options.Applications["MVC"].RootUrl = configuration["App:SelfUrl"];
                        options.RedirectAllowedUrls.AddRange(configuration["App:RedirectAllowedUrls"]?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>());
                    });

                    {{swagger}}

                    context.Services.AddCors(options =>
                    {
                        options.AddDefaultPolicy(builder =>
                        {
                            builder
                                .WithOrigins(configuration["App:CorsOrigins"]?
                                    .Split(",", StringSplitOptions.RemoveEmptyEntries)
                                    .Select(o => o.Trim().TrimEnd('/'))
                                    .ToArray() ?? Array.Empty<string>())
                                .WithAbpExposedHeaders()
                                .SetIsOriginAllowedToAllowWildcardSubdomains()
                                .AllowAnyHeader()
                                .AllowAnyMethod()
                                .AllowCredentials();
                        });
                    });
            {{carriedBlock}}    }

                public override void OnApplicationInitialization(ApplicationInitializationContext context)
                {
                    var app = context.GetApplicationBuilder();
                    var env = context.GetEnvironment();
                    var configuration = context.GetConfiguration();

                    if (env.IsDevelopment())
                    {
                        app.UseDeveloperExceptionPage();
                    }

                    app.UseAbpRequestLocalization();
                    app.UseCorrelationId();
                    app.MapAbpStaticAssets();
                    app.UseRouting();
                    app.UseCors();
                    app.UseAuthentication();
            {{(identity ? "        app.UseAbpOpenIddictValidation();" + Environment.NewLine : "")}}        app.UseMultiTenancy();
                    app.UseUnitOfWork();
            {{(identity ? "        app.UseDynamicClaims();" + Environment.NewLine : "")}}        app.UseAuthorization();
                    app.UseSwagger();
                    app.UseAbpSwaggerUI(options =>
                    {
                        options.SwaggerEndpoint("/swagger/v1/swagger.json", "{{scope}} API");
            {{(identity ? """
                        options.OAuthClientId(configuration["AuthServer:SwaggerClientId"]);
                        options.OAuthScopes(ApiScope);

            """ : "")}}            });
                    app.UseAuditing();
                    app.UseAbpSerilogEnrichers();
                    {{endpoints}}
                }
            }

            """;
    }

    /// <summary>
    /// Keeps the old host module compiled under a new name ({Prefix}HttpApiHostCustomModule, the name
    /// {Prefix}HttpApiHostModule belongs to the generated module). Returns its fully qualified name.
    /// </summary>
    private static string? KeepAsCustomModule(MigrationContext context, string file, string prefix)
    {
        var text = File.ReadAllText(file);
        var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
        var module = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => SyntaxHelpers.HasBaseType(c, "AbpModule"));
        if (module is null) return null;

        // Nothing worth keeping: only empty lifecycle methods / comments.
        var hasCode = module.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Body?.Statements.Count > 0) ||
                      module.Members.Any(m => m is not MethodDeclarationSyntax and not ConstructorDeclarationSyntax and not FieldDeclarationSyntax);
        if (!hasCode) return null;

        var oldName = module.Identifier.Text;
        var newName = $"{prefix}HttpApiHostCustomModule";
        var renamed = System.Text.RegularExpressions.Regex.Replace(text, $@"\b{System.Text.RegularExpressions.Regex.Escape(oldName)}\b", newName);
        var target = Path.Combine(Path.GetDirectoryName(file)!, newName + ".cs");
        FileSystem.WriteText(target, renamed);
        if (!string.Equals(target, file, StringComparison.OrdinalIgnoreCase)) File.Delete(file);

        var ns = module.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
        context.Report.Change("ABP.io host and DbMigrator", $"Kept the old host module as {newName} (background workers, settings...); {prefix}HttpApiHostModule depends on it",
            context.Relative(target));
        return ns is null ? newName : $"global::{ns}.{newName}";
    }

    /// <summary>endpoints.MapHub&lt;ChatHub&gt;("/chatHub") of the old Startup, hub types fully qualified (they keep their routes).</summary>
    private static IEnumerable<(string Hub, string Arguments)> MapHubCalls(MigrationContext context, ClassDeclarationSyntax startup)
    {
        var hubNamespaces = context.Projects.Where(p => p.Layer != ProjectLayer.Tests)
            .SelectMany(p => FileSystem.EnumerateFiles(p.Directory, "*.cs"))
            .Where(f => !context.IsExcluded(f) && File.ReadAllText(f).Contains("Hub", StringComparison.Ordinal))
            .SelectMany(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f)).GetCompilationUnitRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            .Where(c => c.BaseList is not null && c.BaseList.Types.Any(t => SyntaxHelpers.SimpleName(t.Type) is "Hub" or "AbpHub"))
            .GroupBy(c => c.Identifier.Text)
            .ToDictionary(g => g.Key, g => g.First().Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString(), StringComparer.Ordinal);

        foreach (var call in startup.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is not MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.Text: "MapHub", TypeArgumentList.Arguments.Count: 1 } name }) continue;
            var hub = SyntaxHelpers.SimpleName(name.TypeArgumentList.Arguments[0]);
            if (!hubNamespaces.TryGetValue(hub, out var ns)) continue; // Boilerplate's AbpCommonHub and other framework hubs
            yield return (ns is null ? hub : $"global::{ns}.{hub}", call.ArgumentList.ToString());
        }
    }

    /// <summary>
    /// Whether an ABP.io module is used. This step runs before the project files are converted, so the csproj may still
    /// reference the Boilerplate package; the converted [DependsOn] already names the ABP.io module.
    /// </summary>
    private static bool UsesAbpModule(MigrationContext context, string module, params string[] packages) =>
        context.Projects.Any(p =>
            packages.Any(package => Regex.IsMatch(File.ReadAllText(p.CsprojPath), $@"Include=""{Regex.Escape(package)}""", RegexOptions.IgnoreCase)) ||
            FileSystem.EnumerateFiles(p.Directory, "*.cs").Where(f => !context.IsExcluded(f))
                .Any(f => File.ReadAllText(f).Contains($"typeof({module})", StringComparison.Ordinal)));

    /// <summary>The route argument of MapHub("/route", options...) — the first argument.</summary>
    private static string HubRoute(string arguments)
    {
        var list = SyntaxFactory.ParseArgumentList(arguments);
        return list.Arguments.Count > 0 ? list.Arguments[0].ToString() : "\"/signalr\"";
    }

    private static string HostProgram(string ns, string moduleClass) => $$"""
        using System;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Builder;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Hosting;
        using Serilog;
        using Serilog.Events;

        namespace {{ns}};

        public class Program
        {
            public static async Task<int> Main(string[] args)
            {
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Information()
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
                    .Enrich.FromLogContext()
                    .WriteTo.Async(c => c.File("Logs/logs.txt"))
                    .WriteTo.Async(c => c.Console())
                    .CreateLogger();

                try
                {
                    Log.Information("Starting web host.");
                    var builder = WebApplication.CreateBuilder(args);
                    builder.Host
                        .AddAppSettingsSecretsJson()
                        .UseAutofac()
                        .UseSerilog();
                    await builder.AddApplicationAsync<{{moduleClass}}>();
                    var app = builder.Build();
                    await app.InitializeApplicationAsync();
                    await app.RunAsync();
                    return 0;
                }
                catch (Exception ex)
                {
                    if (ex is HostAbortedException)
                    {
                        throw;
                    }

                    Log.Fatal(ex, "Host terminated unexpectedly!");
                    return 1;
                }
                finally
                {
                    await Log.CloseAndFlushAsync();
                }
            }
        }

        """;

    // ---------- appsettings ----------

    private static JsonObject ReadSettings(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        try
        {
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                   ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static void WriteSettings(string path, JsonObject settings) =>
        FileSystem.WriteText(path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + Environment.NewLine);

    private static JsonObject Section(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static void SetIfMissing(JsonObject section, string key, string value)
    {
        if (section[key] is null) section[key] = value;
    }

    /// <summary>
    /// Boilerplate used Redis only when configured (Configuration.Caching.UseRedis with Abp:RedisCache:ConnectionString);
    /// ABP.io's AbpCachingStackExchangeRedisModule uses Redis unless Redis:IsEnabled is false, and fails without
    /// Redis:Configuration. The Boilerplate values are carried over; without a connection string Redis is disabled
    /// (in-memory cache, as before).
    /// </summary>
    private void MergeRedisSettings(MigrationContext context, ProjectInfo host, JsonObject settings)
    {
        var redis = Section(settings, "Redis");
        if (redis["Configuration"] is not null) return;

        var boilerplate = (settings["Abp"] as JsonObject)?["RedisCache"] as JsonObject;
        var connection = boilerplate?["ConnectionString"]?.GetValue<string>();
        var database = boilerplate?["DatabaseId"]?.ToString();
        if (string.IsNullOrWhiteSpace(connection))
        {
            redis["IsEnabled"] = "false";
            redis["Configuration"] = "127.0.0.1";
            context.Report.Change(Name, "Redis:IsEnabled = false (no Abp:RedisCache:ConnectionString: in-memory cache as in Boilerplate); " +
                                        "set Redis:Configuration and Redis:IsEnabled = true to use Redis", context.Relative(host.CsprojPath));
            return;
        }

        redis["IsEnabled"] = "true";
        redis["Configuration"] = string.IsNullOrWhiteSpace(database) || database == "-1" ? connection : $"{connection},defaultDatabase={database}";
        context.Report.Change(Name, "Redis:Configuration set from Abp:RedisCache:ConnectionString", context.Relative(host.CsprojPath));
    }

    /// <summary>Adds the ABP.io host settings, reusing the Boilerplate App:ServerRootAddress / ClientRootAddress / CorsOrigins.</summary>
    private static void MergeHostSettings(JsonObject settings, string scope, bool identity, string passPhrase)
    {
        var app = Section(settings, "App");
        var self = (app["ServerRootAddress"]?.GetValue<string>() ?? "https://localhost:44300").TrimEnd('/');
        var client = (app["ClientRootAddress"]?.GetValue<string>() ?? "http://localhost:4200").TrimEnd('/');
        SetIfMissing(app, "SelfUrl", self);
        SetIfMissing(app, "CorsOrigins", client);
        SetIfMissing(app, "RedirectAllowedUrls", client);

        if (identity)
        {
            var auth = Section(settings, "AuthServer");
            SetIfMissing(auth, "Authority", self);
            SetIfMissing(auth, "RequireHttpsMetadata", "false");
            SetIfMissing(auth, "SwaggerClientId", $"{scope}_Swagger");
            AddOpenIddictApplications(settings, scope, client, self);
        }

        SetIfMissing(Section(settings, "StringEncryption"), "DefaultPassPhrase", passPhrase);
    }

    private static void AddOpenIddictApplications(JsonObject settings, string scope, string client, string self)
    {
        var applications = Section(Section(settings, "OpenIddict"), "Applications");
        var app = Section(applications, $"{scope}_App");
        SetIfMissing(app, "ClientId", $"{scope}_App");
        SetIfMissing(app, "RootUrl", client);
        var swagger = Section(applications, $"{scope}_Swagger");
        SetIfMissing(swagger, "ClientId", $"{scope}_Swagger");
        SetIfMissing(swagger, "RootUrl", self);
    }

    // ---------- OpenIddict seeding ----------

    private void WriteOpenIddictSeeder(MigrationContext context, ProjectInfo domain, string prefix)
    {
        var ns = domain.RootNamespace + ".OpenIddict";
        var path = Path.Combine(domain.Directory, "OpenIddict", $"{prefix}OpenIddictDataSeedContributor.cs");
        FileSystem.WriteText(path, $$"""
            using System;
            using System.Threading.Tasks;
            using Microsoft.Extensions.Configuration;
            using OpenIddict.Abstractions;
            using Volo.Abp.Data;
            using Volo.Abp.DependencyInjection;
            using static OpenIddict.Abstractions.OpenIddictConstants;

            namespace {{ns}};

            /* Creates the API scope and the client applications (SPA + Swagger) defined in OpenIddict:Applications of appsettings.json.
             * Generated by AbpToVolo, based on the ABP.io template's OpenIddictDataSeedContributor. */
            public class {{prefix}}OpenIddictDataSeedContributor : IDataSeedContributor, ITransientDependency
            {
                private const string ApiScope = "{{prefix}}";

                private readonly IConfiguration _configuration;
                private readonly IOpenIddictApplicationManager _applicationManager;
                private readonly IOpenIddictScopeManager _scopeManager;

                public {{prefix}}OpenIddictDataSeedContributor(
                    IConfiguration configuration,
                    IOpenIddictApplicationManager applicationManager,
                    IOpenIddictScopeManager scopeManager)
                {
                    _configuration = configuration;
                    _applicationManager = applicationManager;
                    _scopeManager = scopeManager;
                }

                public async Task SeedAsync(DataSeedContext context)
                {
                    if (await _scopeManager.FindByNameAsync(ApiScope) is null)
                    {
                        await _scopeManager.CreateAsync(new OpenIddictScopeDescriptor
                        {
                            Name = ApiScope,
                            DisplayName = ApiScope + " API",
                            Resources = { ApiScope }
                        });
                    }

                    var applications = _configuration.GetSection("OpenIddict:Applications");
                    await CreateClientAsync(applications[$"{ApiScope}_App:ClientId"], applications[$"{ApiScope}_App:RootUrl"], isSwagger: false);
                    await CreateClientAsync(applications[$"{ApiScope}_Swagger:ClientId"], applications[$"{ApiScope}_Swagger:RootUrl"], isSwagger: true);
                }

                private async Task CreateClientAsync(string clientId, string rootUrl, bool isSwagger)
                {
                    if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(rootUrl) ||
                        await _applicationManager.FindByClientIdAsync(clientId) is not null)
                    {
                        return;
                    }

                    var root = rootUrl.TrimEnd('/');
                    var descriptor = new OpenIddictApplicationDescriptor
                    {
                        ClientId = clientId,
                        ClientType = ClientTypes.Public,
                        ConsentType = ConsentTypes.Implicit,
                        DisplayName = clientId
                    };

                    descriptor.RedirectUris.Add(new Uri(isSwagger ? root + "/swagger/oauth2-redirect.html" : root));
                    if (!isSwagger)
                    {
                        descriptor.PostLogoutRedirectUris.Add(new Uri(root));
                        descriptor.Permissions.Add(Permissions.GrantTypes.Password);
                    }

                    foreach (var permission in new[]
                             {
                                 Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.Endpoints.EndSession,
                                 Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken, Permissions.ResponseTypes.Code,
                                 Permissions.Scopes.Email, Permissions.Scopes.Profile, Permissions.Scopes.Roles, Permissions.Prefixes.Scope + ApiScope
                             })
                    {
                        descriptor.Permissions.Add(permission);
                    }

                    await _applicationManager.CreateAsync(descriptor);
                }
            }

            """);
        context.Report.Change(Name, $"Generated {prefix}OpenIddictDataSeedContributor (API scope, SPA and Swagger clients)", context.Relative(path));
    }

    // ---------- DbMigrator ----------

    /// <summary>
    /// "dotnet ef" design-time factory, ABP.io template style: the configuration comes from the DbMigrator's appsettings.json
    /// (Boilerplate's AppConfigurations/WebContentDirectoryFinder looked for the old *.Web.Host folder), and the entity
    /// extension mappings are configured first — otherwise "migrations add" would not see the extra properties of the
    /// Identity entities and would drop their columns.
    /// </summary>
    private void StandardizeDesignTimeFactory(MigrationContext context, ProjectInfo efCore, ProjectInfo migrator, string prefix)
    {
        var file = FileSystem.EnumerateFiles(efCore.Directory, "*.cs")
            .FirstOrDefault(f => !context.IsExcluded(f) && File.ReadAllText(f).Contains("IDesignTimeDbContextFactory<", StringComparison.Ordinal));
        if (file is null) return;

        var (code, encoding) = FileSystem.ReadText(file);
        var root = CSharpSyntaxTree.ParseText(code).GetCompilationUnitRoot();
        var create = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == "CreateDbContext" && m.Body is not null);
        if (create is null) return;

        var configuration = create.Body!.Statements.OfType<LocalDeclarationStatementSyntax>()
            .FirstOrDefault(s => s.Declaration.Variables.Count == 1 && s.Declaration.Variables[0].Identifier.Text == "configuration");
        var mappings = $"{prefix}EfCoreEntityExtensionMappings";
        var hasMappings = FileSystem.EnumerateFiles(efCore.Directory, "*.cs").Any(f => File.ReadAllText(f).Contains($"class {mappings}", StringComparison.Ordinal));
        if (configuration is null && !hasMappings) return;

        var cls = create.Ancestors().OfType<ClassDeclarationSyntax>().First();
        var nl = code.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var migratorFolder = Path.GetFileName(migrator.Directory);
        var updatedCreate = create;
        if (configuration is not null)
        {
            var replacement = SyntaxFactory.ParseStatement("var configuration = BuildConfiguration();").WithTriviaFrom(configuration);
            updatedCreate = updatedCreate.ReplaceNode(updatedCreate.Body!.Statements.First(s => s.IsEquivalentTo(configuration)), replacement);
        }

        if (hasMappings && !code.Contains($"{mappings}.Configure()", StringComparison.Ordinal))
        {
            var first = updatedCreate.Body!.Statements.First();
            var call = SyntaxFactory.ParseStatement($"{mappings}.Configure();").WithLeadingTrivia(first.GetLeadingTrivia()).WithTrailingTrivia(SyntaxFactory.EndOfLine(nl));
            updatedCreate = updatedCreate.WithBody(updatedCreate.Body.WithStatements(updatedCreate.Body.Statements.Insert(0, call)));
        }

        var updatedClass = cls.ReplaceNode(cls.Members.OfType<MethodDeclarationSyntax>().First(m => m.Identifier.Text == "CreateDbContext"), updatedCreate);
        if (configuration is not null && !cls.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.Text == "BuildConfiguration"))
        {
            var indent = SyntaxHelpers.Indentation(create);
            var method = SyntaxFactory.ParseMemberDeclaration(
                $"{nl}{indent}/* ABP.io template: the design-time configuration is the DbMigrator's appsettings.json (run \"dotnet ef\" in this project folder). */{nl}" +
                $"{indent}private static IConfigurationRoot BuildConfiguration() =>{nl}" +
                $"{indent}    new ConfigurationBuilder(){nl}" +
                $"{indent}        .SetBasePath(Path.Combine(Directory.GetCurrentDirectory(), \"../{migratorFolder}/\")){nl}" +
                $"{indent}        .AddJsonFile(\"appsettings.json\", optional: false){nl}" +
                $"{indent}        .Build();{nl}")!;
            updatedClass = updatedClass.AddMembers(method);
        }

        var result = ModuleMethodEditor.AddUsings(root.ReplaceNode(cls, updatedClass).ToFullString(), ["System.IO", "Microsoft.Extensions.Configuration"]);
        FileSystem.WriteText(file, result, encoding);
        context.Report.Change(Name, $"{cls.Identifier.Text}: design-time configuration from {migratorFolder}/appsettings.json" +
                                    (hasMappings ? $", {mappings}.Configure() first" : ""), context.Relative(file));
    }

    private void WriteDbMigration(MigrationContext context, ProjectInfo domain, ProjectInfo efCore, string prefix, Modules modules, JsonObject? hostSettings)
    {
        var identity = context.UsesIdentityModules;
        var dataNs = domain.RootNamespace + ".Data";
        FileSystem.WriteText(Path.Combine(domain.Directory, "Data", $"I{prefix}DbSchemaMigrator.cs"), $$"""
            using System.Threading.Tasks;

            namespace {{dataNs}};

            public interface I{{prefix}}DbSchemaMigrator
            {
                Task MigrateAsync();
            }

            """);

        FileSystem.WriteText(Path.Combine(domain.Directory, "Data", $"{prefix}DbMigrationService.cs"), DbMigrationService(dataNs, prefix, identity));

        FileSystem.WriteText(Path.Combine(efCore.Directory, "EntityFrameworkCore", $"EntityFrameworkCore{prefix}DbSchemaMigrator.cs"), $$"""
            using System;
            using System.Threading.Tasks;
            using Microsoft.EntityFrameworkCore;
            using Microsoft.Extensions.DependencyInjection;
            using Volo.Abp.DependencyInjection;
            using {{dataNs}};

            namespace {{efCore.RootNamespace}}.EntityFrameworkCore;

            public class EntityFrameworkCore{{prefix}}DbSchemaMigrator : I{{prefix}}DbSchemaMigrator, ITransientDependency
            {
                private readonly IServiceProvider _serviceProvider;

                public EntityFrameworkCore{{prefix}}DbSchemaMigrator(IServiceProvider serviceProvider)
                {
                    _serviceProvider = serviceProvider;
                }

                public async Task MigrateAsync()
                {
                    /* The DbContext is resolved from the service provider (not injected) so that the current tenant's
                     * connection string is used. */
                    var dbContext = _serviceProvider.GetRequiredService<{{modules.DbContext}}>();

                    /* Converting an existing database (ConvertModuleZeroKeys, data copy) can take longer than the default 30 s. */
                    dbContext.Database.SetCommandTimeout(TimeSpan.FromMinutes(30));
                    await dbContext.Database.MigrateAsync();
                }
            }

            """);

        // The DbMigrator project: reuse the converted Migrator, or create it.
        var migrator = context.FindProject(ProjectLayer.DbMigrator);
        if (migrator is null)
        {
            var baseName = ContractsScaffoldStep.BaseName(domain);
            var directory = Path.Combine(Path.GetDirectoryName(domain.Directory)!, baseName + ".DbMigrator");
            var references = new[] { efCore.CsprojPath, context.FindProject(ProjectLayer.ApplicationContracts)?.CsprojPath ?? context.FindProject(ProjectLayer.Application)?.CsprojPath }
                .OfType<string>();
            var csproj = ProjectWriter.CreateProject(directory, baseName + ".DbMigrator", context.Options.TargetFramework, domain.RootNamespace + ".DbMigrator", references,
                properties: new Dictionary<string, string> { ["OutputType"] = "Exe" });
            var doc = System.Xml.Linq.XDocument.Load(csproj);
            doc.Root!.Add(new System.Xml.Linq.XElement("ItemGroup",
                new System.Xml.Linq.XElement("None", new System.Xml.Linq.XAttribute("Update", "appsettings.json"),
                    new System.Xml.Linq.XElement("CopyToOutputDirectory", "PreserveNewest"))));
            ProjectWriter.Save(doc, csproj);
            if (ProjectWriter.FindSolution(context.OutputRoot) is { } solution && ProjectWriter.AddToSolution(solution, csproj, "src") is { } error)
            {
                context.Report.Warning(Name, $"Could not add the DbMigrator to the solution: {error}");
            }

            context.RefreshProjects();
            migrator = context.Projects.First(p => p.CsprojPath == csproj);
            context.Report.Change(Name, $"Created project {migrator.Name}", context.Relative(csproj));
        }
        else
        {
            var moved = FileSystem.EnumerateFiles(migrator.Directory, "*.cs").Where(f => !context.IsExcluded(f)).ToList();
            foreach (var file in moved) MoveToLegacy(migrator, file);
            if (moved.Count > 0)
            {
                ModuleZeroStep.ExcludeLegacyFolder(migrator.CsprojPath);
                context.Report.Change(Name, $"Moved the Boilerplate Migrator code to {FileSystem.LegacyFolder} ({moved.Count} files)", context.Relative(migrator.CsprojPath));
            }

            // The DbMigrator module depends on the EF Core and contracts modules (ABP.io template).
            foreach (var reference in new[] { efCore, context.FindProject(ProjectLayer.ApplicationContracts) ?? context.FindProject(ProjectLayer.Application) }.OfType<ProjectInfo>())
            {
                ProjectWriter.AddProjectReference(migrator.CsprojPath, reference.CsprojPath);
            }
        }

        StandardizeDesignTimeFactory(context, efCore, migrator, prefix);

        var migratorNs = migrator.RootNamespace;
        var moduleClass = $"{prefix}DbMigratorModule";
        var dependsOn = new[] { "AbpAutofacModule", modules.EfCore, modules.Contracts ?? modules.Application }.OfType<string>();
        FileSystem.WriteText(Path.Combine(migrator.Directory, moduleClass + ".cs"), $$"""
            using Volo.Abp.Autofac;
            using Volo.Abp.Modularity;

            namespace {{migratorNs}};

            [DependsOn(
            {{string.Join("," + Environment.NewLine, dependsOn.Select(d => $"    typeof({d})"))}})]
            public class {{moduleClass}} : AbpModule
            {
            }

            """);

        FileSystem.WriteText(Path.Combine(migrator.Directory, "DbMigratorHostedService.cs"), $$"""
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.Extensions.Configuration;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            using Serilog;
            using Volo.Abp;
            using Volo.Abp.Data;
            using {{dataNs}};

            namespace {{migratorNs}};

            public class DbMigratorHostedService : IHostedService
            {
                private readonly IHostApplicationLifetime _hostApplicationLifetime;
                private readonly IConfiguration _configuration;

                public DbMigratorHostedService(IHostApplicationLifetime hostApplicationLifetime, IConfiguration configuration)
                {
                    _hostApplicationLifetime = hostApplicationLifetime;
                    _configuration = configuration;
                }

                public async Task StartAsync(CancellationToken cancellationToken)
                {
                    using (var application = await AbpApplicationFactory.CreateAsync<{{moduleClass}}>(options =>
                           {
                               options.Services.ReplaceConfiguration(_configuration);
                               options.UseAutofac();
                               options.Services.AddLogging(c => c.AddSerilog());
                               options.AddDataMigrationEnvironment();
                           }))
                    {
                        await application.InitializeAsync();

                        await application
                            .ServiceProvider
                            .GetRequiredService<{{prefix}}DbMigrationService>()
                            .MigrateAsync();

                        await application.ShutdownAsync();

                        _hostApplicationLifetime.StopApplication();
                    }
                }

                public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }

            """);

        FileSystem.WriteText(Path.Combine(migrator.Directory, "Program.cs"), $$"""
            using System.Threading.Tasks;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            using Microsoft.Extensions.Logging;
            using Serilog;
            using Serilog.Events;

            namespace {{migratorNs}};

            public class Program
            {
                public static async Task Main(string[] args)
                {
                    Log.Logger = new LoggerConfiguration()
                        .MinimumLevel.Information()
                        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                        .MinimumLevel.Override("Volo.Abp", LogEventLevel.Warning)
                        .Enrich.FromLogContext()
                        .WriteTo.Async(c => c.File("Logs/logs.txt"))
                        .WriteTo.Async(c => c.Console())
                        .CreateLogger();

                    await CreateHostBuilder(args).RunConsoleAsync();
                }

                public static IHostBuilder CreateHostBuilder(string[] args) =>
                    Host.CreateDefaultBuilder(args)
                        .AddAppSettingsSecretsJson()
                        .ConfigureLogging((context, logging) => logging.ClearProviders())
                        .ConfigureServices((hostContext, services) =>
                        {
                            services.AddHostedService<DbMigratorHostedService>();
                        });
            }

            """);

        foreach (var (package, version) in SerilogConsolePackages)
        {
            ProjectWriter.AddPackageReference(migrator.CsprojPath, package, version);
        }

        ProjectWriter.AddPackageReference(migrator.CsprojPath, "Volo.Abp.Autofac", context.Options.AbpVersion);
        ProjectWriter.AddPackageReference(migrator.CsprojPath, "Microsoft.Extensions.Hosting", context.MicrosoftPackagesVersion);

        // appsettings: connection string (from the host when missing) + OpenIddict clients for the seeder.
        var settingsPath = Path.Combine(migrator.Directory, "appsettings.json");
        var settings = ReadSettings(settingsPath);
        if (settings["ConnectionStrings"] is null && hostSettings?["ConnectionStrings"] is JsonObject connectionStrings)
        {
            settings["ConnectionStrings"] = connectionStrings.DeepClone();
        }

        if (context.Options.ConnectionString is { } connectionString)
        {
            Section(settings, "ConnectionStrings")["Default"] = connectionString;
        }
        else if (settings["ConnectionStrings"]?["Default"]?.ToString() is { } migratorDb && hostSettings?["ConnectionStrings"]?["Default"]?.ToString() is { } hostDb &&
                 !string.Equals(migratorDb, hostDb, StringComparison.Ordinal))
        {
            context.Report.Warning(Name, $"The connection strings of {migrator.Name} and the host differ (the DbMigrator migrates its own one); " +
                                         "use --connection-string to set both.", context.Relative(settingsPath));
        }

        if (identity && hostSettings is not null)
        {
            var client = hostSettings["App"]?["ClientRootAddress"]?.GetValue<string>()?.TrimEnd('/') ?? "http://localhost:4200";
            var self = hostSettings["App"]?["SelfUrl"]?.GetValue<string>() ?? "https://localhost:44300";
            AddOpenIddictApplications(settings, prefix, client, self);
        }

        WriteSettings(settingsPath, settings);
        context.Report.Change(Name, $"Generated the ABP.io DbMigrator ({moduleClass}, DbMigratorHostedService, {prefix}DbMigrationService, " +
                                    $"EntityFrameworkCore{prefix}DbSchemaMigrator)", context.Relative(migrator.CsprojPath));
        context.Report.Todo(Name, $"Run the {migrator.Name} project to apply migrations and seed data (admin user, OpenIddict clients, permissions).",
            context.Relative(migrator.CsprojPath));
    }

    private static string DbMigrationService(string ns, string prefix, bool identity)
    {
        var tenants = identity
            ? """

                    var tenants = await _tenantRepository.GetListAsync(includeDetails: true);
                    foreach (var tenant in tenants)
                    {
                        using (_currentTenant.Change(tenant.Id))
                        {
                            if (tenant.ConnectionStrings.Count > 0)
                            {
                                await MigrateDatabaseSchemaAsync();
                            }

                            await SeedDataAsync(tenant.Id);
                        }

                        Logger.LogInformation("Successfully completed {TenantName} tenant database migrations.", tenant.Name);
                    }
            """
            : "";

        var seed = identity
            ? """
                    await _dataSeeder.SeedAsync(new DataSeedContext(tenantId)
                        .WithProperty(IdentityDataSeedContributor.AdminEmailPropertyName, IdentityDataSeedContributor.AdminEmailDefaultValue)
                        .WithProperty(IdentityDataSeedContributor.AdminPasswordPropertyName, IdentityDataSeedContributor.AdminPasswordDefaultValue));
            """
            : "        await _dataSeeder.SeedAsync(new DataSeedContext(tenantId));";

        return $$"""
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Microsoft.Extensions.Logging;
            using Microsoft.Extensions.Logging.Abstractions;
            using Volo.Abp.Data;
            using Volo.Abp.DependencyInjection;
            using Volo.Abp.MultiTenancy;
            {{(identity ? "using Volo.Abp.Identity;" + Environment.NewLine + "using Volo.Abp.TenantManagement;" + Environment.NewLine : "")}}
            namespace {{ns}};

            /* Applies the EF Core migrations and seeds the data (generated by AbpToVolo from the ABP.io template). */
            public class {{prefix}}DbMigrationService : ITransientDependency
            {
                public ILogger<{{prefix}}DbMigrationService> Logger { get; set; }

                private readonly IDataSeeder _dataSeeder;
                private readonly IEnumerable<I{{prefix}}DbSchemaMigrator> _dbSchemaMigrators;
                private readonly ICurrentTenant _currentTenant;
            {{(identity ? "    private readonly ITenantRepository _tenantRepository;" + Environment.NewLine : "")}}
                public {{prefix}}DbMigrationService(
                    IDataSeeder dataSeeder,
                    IEnumerable<I{{prefix}}DbSchemaMigrator> dbSchemaMigrators,
                    ICurrentTenant currentTenant{{(identity ? "," + Environment.NewLine + "        ITenantRepository tenantRepository" : "")}})
                {
                    _dataSeeder = dataSeeder;
                    _dbSchemaMigrators = dbSchemaMigrators;
                    _currentTenant = currentTenant;
            {{(identity ? "        _tenantRepository = tenantRepository;" + Environment.NewLine : "")}}
                    Logger = NullLogger<{{prefix}}DbMigrationService>.Instance;
                }

                public async Task MigrateAsync()
                {
                    Logger.LogInformation("Started database migrations...");

                    await MigrateDatabaseSchemaAsync();
                    await SeedDataAsync(null);

                    Logger.LogInformation("Successfully completed host database migrations.");
            {{tenants}}
                    Logger.LogInformation("Successfully completed all database migrations.");
                }

                private async Task MigrateDatabaseSchemaAsync()
                {
                    foreach (var migrator in _dbSchemaMigrators)
                    {
                        await migrator.MigrateAsync();
                    }
                }

                private async Task SeedDataAsync(Guid? tenantId)
                {
            {{seed}}
                }
            }

            """;
    }

    // ---------- helpers ----------

    private static void MoveToLegacy(ProjectInfo project, string file)
    {
        var target = Path.Combine(project.Directory, FileSystem.LegacyFolder, Path.GetRelativePath(project.Directory, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(file, target, overwrite: true);
    }

    private static string? ModuleName(MigrationContext context, ProjectLayer layer)
    {
        var project = context.FindProject(layer);
        if (project is null || !AbpModulesStep.ModuleNameFilters.TryGetValue(layer, out var filter)) return null;
        var file = AbpModulesStep.FindModuleFile(project, filter);
        if (file is null) return null;

        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetCompilationUnitRoot();
        var module = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First(c => SyntaxHelpers.HasBaseType(c, "AbpModule") && filter(c.Identifier.Text));
        var ns = module.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault() is { } n ? SolutionModel.FullNamespace(n) : null;
        return ns is null ? module.Identifier.Text : $"global::{ns}.{module.Identifier.Text}";
    }

    private static string? DbContextName(ProjectInfo efCore)
    {
        foreach (var file in FileSystem.EnumerateFiles(efCore.Directory, "*.cs"))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("AbpDbContext<", StringComparison.Ordinal)) continue;
            var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
            var cls = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => SyntaxHelpers.HasBaseType(c, "AbpDbContext"));
            if (cls is null) continue;
            var ns = cls.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault() is { } n ? SolutionModel.FullNamespace(n) : null;
            return ns is null ? cls.Identifier.Text : $"global::{ns}.{cls.Identifier.Text}";
        }

        return null;
    }
}
