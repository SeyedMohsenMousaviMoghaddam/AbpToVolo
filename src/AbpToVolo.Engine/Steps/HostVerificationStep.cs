using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AbpToVolo.Core;

namespace AbpToVolo.Steps;

/// <summary>
/// Runs the converted application (opt-in):
///   --migrate-database: runs the DbMigrator on the configured database (migrations + seed) — it changes that database;
///   --verify-host: starts the HttpApi.Host on a free local port, waits for /swagger/v1/swagger.json and
///   /swagger/index.html, then stops it.
/// The result (and the first errors of the logs) goes into the report, so every conversion shows whether the converted
/// solution really starts.
/// </summary>
public sealed partial class HostVerificationStep : IMigrationStep
{
    public string Name => "Run the converted application";

    public bool IsEnabled(MigrationOptions options) => options.MigrateDatabase || options.VerifyHost;

    public static readonly TimeSpan MigratorTimeout = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan HostTimeout = TimeSpan.FromMinutes(5);

    public void Execute(MigrationContext context)
    {
        var lines = context.Report.Section("Run the converted application");

        if (context.Options.MigrateDatabase)
        {
            var migrator = context.FindProject(ProjectLayer.DbMigrator);
            if (migrator is null)
            {
                context.Report.Warning(Name, "--migrate-database: no DbMigrator project.");
            }
            else
            {
                context.Log.WriteLine($"    dotnet run --project {migrator.Name} (migrations + seed) ...");
                var (ok, output) = RunToEnd(migrator.CsprojPath, migrator.Directory, MigratorTimeout, context.CancellationToken);
                var errors = Errors(output);
                lines.Add(ok ? $"- DbMigrator: database migrated and seeded." : $"- DbMigrator **failed**:{Environment.NewLine}{Quote(errors)}");
                if (ok) context.Report.Change(Name, "DbMigrator: migrations applied, data seeded", context.Relative(migrator.CsprojPath));
                else context.Report.Warning(Name, "DbMigrator failed: " + string.Join(" | ", errors.Take(3)), context.Relative(migrator.CsprojPath));
                context.Log.WriteLine(ok ? "    DbMigrator: OK" : "    DbMigrator FAILED: " + string.Join(" | ", errors.Take(3)));
            }
        }

        if (context.Options.VerifyHost)
        {
            var host = context.FindProject(ProjectLayer.Host);
            if (host is null)
            {
                context.Report.Warning(Name, "--verify-host: no HttpApi.Host project.");
                return;
            }

            var result = VerifyHost(context, host);
            lines.Add(result.Ok
                ? $"- Host started; Swagger answers ({result.Paths} API paths in /swagger/v1/swagger.json)."
                : $"- Host check **failed**: {result.Summary}{Environment.NewLine}{Quote(result.Errors)}");
            if (result.Ok)
            {
                context.Report.Change(Name, $"Host started and Swagger answers ({result.Paths} API paths)", context.Relative(host.CsprojPath));
            }
            else
            {
                context.Report.Warning(Name, $"Host check failed: {result.Summary} " + string.Join(" | ", result.Errors.Take(3)), context.Relative(host.CsprojPath));
            }

            context.Log.WriteLine(result.Ok ? $"    Swagger: OK ({result.Paths} API paths)" : $"    Swagger FAILED: {result.Summary}");
        }
    }

    private sealed record HostResult(bool Ok, int Paths, string Summary, List<string> Errors);

    private static HostResult VerifyHost(MigrationContext context, ProjectInfo host)
    {
        // Kestrel:Endpoints of appsettings.json win over ASPNETCORE_URLS: probe that address then.
        var configured = KestrelUrl(Path.Combine(host.Directory, "appsettings.json"));
        var url = configured ?? $"http://localhost:{FreePort()}";
        context.Log.WriteLine($"    dotnet run --project {host.Name} on {url} ...");

        var log = new StringBuilder();
        var environment = new List<(string, string)> { ("ASPNETCORE_ENVIRONMENT", "Development"), ("DOTNET_ENVIRONMENT", "Development") };
        if (configured is null) environment.Add(("ASPNETCORE_URLS", url));
        using var process = Start(host.CsprojPath, host.Directory, log, environment.ToArray());
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            var deadline = DateTime.UtcNow + HostTimeout;
            HttpResponseMessage? json = null;
            while (DateTime.UtcNow < deadline && !process.HasExited)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                try
                {
                    json = http.GetAsync($"{url}/swagger/v1/swagger.json", context.CancellationToken).GetAwaiter().GetResult();
                    break;
                }
                catch (HttpRequestException)
                {
                    Thread.Sleep(2000); // not listening yet
                }
            }

            if (json is null)
            {
                var reason = process.HasExited ? $"the host stopped (exit code {process.ExitCode})" : $"no answer on {url} after {HostTimeout.TotalMinutes} minutes";
                var errors = Errors(Snapshot(log));
                if (errors.Count == 0) errors = Snapshot(log).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(8).ToList();
                return new HostResult(false, 0, reason, errors);
            }

            var body = json.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (json.StatusCode != HttpStatusCode.OK)
            {
                return new HostResult(false, 0, $"/swagger/v1/swagger.json returned {(int)json.StatusCode}", Errors(Snapshot(log) + Environment.NewLine + body));
            }

            var page = http.GetAsync($"{url}/swagger/index.html", context.CancellationToken).GetAwaiter().GetResult();
            if (page.StatusCode != HttpStatusCode.OK)
            {
                return new HostResult(false, 0, $"/swagger/index.html returned {(int)page.StatusCode}", Errors(Snapshot(log)));
            }

            var paths = SwaggerPath().Matches(body).Count;
            return new HostResult(true, paths, "OK", []);
        }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private static (bool Ok, string Output) RunToEnd(string csproj, string directory, TimeSpan timeout, CancellationToken token)
    {
        var log = new StringBuilder();
        using var process = Start(csproj, directory, log);
        var exited = process.WaitForExit((int)timeout.TotalMilliseconds);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            return (false, Snapshot(log) + Environment.NewLine + $"timeout after {timeout.TotalMinutes} minutes");
        }

        process.WaitForExit();
        token.ThrowIfCancellationRequested();
        return (process.ExitCode == 0, Snapshot(log));
    }

    private static Process Start(string csproj, string directory, StringBuilder log, params (string Name, string Value)[] environment)
    {
        var start = new ProcessStartInfo("dotnet", ["run", "--project", csproj, "--no-launch-profile"])
        {
            WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var (name, value) in environment) start.Environment[name] = value;

        var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static string Snapshot(StringBuilder log)
    {
        lock (log) return log.ToString();
    }

    /// <summary>The distinct error lines of a log (exceptions, [ERR]/[FTL], build errors), at most 10.</summary>
    private static List<string> Errors(string output) =>
        output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => ErrorLine().IsMatch(l) && !l.Contains("warning", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Length > 300 ? l[..300] + "…" : l)
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToList();

    private static string Quote(List<string> lines) =>
        lines.Count == 0 ? "  (no error line in the output)" : string.Join(Environment.NewLine, lines.Select(l => $"  > {l}"));

    /// <summary>The first http(s) URL of Kestrel:Endpoints in appsettings.json ("*"/"+" hosts → localhost), or null.</summary>
    private static string? KestrelUrl(string settings)
    {
        if (!File.Exists(settings)) return null;
        try
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settings),
                documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (json?["Kestrel"]?["Endpoints"] is not System.Text.Json.Nodes.JsonObject endpoints) return null;
            var urls = endpoints.Select(e => e.Value?["Url"]?.GetValue<string>()).OfType<string>().ToList();
            var chosen = urls.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? urls.FirstOrDefault();
            return chosen is null ? null : Regex.Replace(chosen.TrimEnd('/'), @"//(\*|\+|0\.0\.0\.0)(?=[:/]|$)", "//localhost");
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [GeneratedRegex(@"Exception[:\s]|\[(ERR|FTL)\]|: error |Unhandled exception|rows refer to", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLine();

    [GeneratedRegex(@"""(/[^""]*)""\s*:\s*\{\s*""(get|post|put|delete|patch)""", RegexOptions.IgnoreCase)]
    private static partial Regex SwaggerPath();
}
