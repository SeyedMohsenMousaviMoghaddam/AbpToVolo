using System.Diagnostics;
using System.Text.RegularExpressions;
using AbpToVolo.Core;

namespace AbpToVolo.Steps;

/// <summary>Optional (--build): runs "dotnet build" on the converted solution and records the compiler errors.</summary>
public sealed partial class BuildVerificationStep : IMigrationStep
{
    private readonly Func<ProcessStartInfo, CancellationToken, (int ExitCode, string Output)> _run;

    public BuildVerificationStep() : this(RunBuild) { }

    internal BuildVerificationStep(Func<ProcessStartInfo, CancellationToken, (int ExitCode, string Output)> run) => _run = run;

    public string Name => "Build verification";

    public bool IsEnabled(MigrationOptions options) => options.Build;

    public void Execute(MigrationContext context)
    {
        var target = FileSystem.EnumerateFiles(context.OutputRoot, "*.sln")
                         .Concat(FileSystem.EnumerateFiles(context.OutputRoot, "*.slnx"))
                         .OrderBy(f => f.Length)
                         .FirstOrDefault()
                     ?? context.Projects.FirstOrDefault(p => p.Layer == ProjectLayer.Host)?.CsprojPath;

        if (target is null)
        {
            context.Report.Warning(Name, "No solution or host project found to build.");
            return;
        }

        var start = new ProcessStartInfo("dotnet", ["build", target, "-nologo", "-clp:NoSummary", "-v:q"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = context.OutputRoot
        };

        context.CancellationToken.ThrowIfCancellationRequested();
        var (exitCode, output) = _run(start, context.CancellationToken);

        var errors = output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => ErrorLine().IsMatch(l))
            .Select(l => l.Replace(context.OutputRoot, "", StringComparison.OrdinalIgnoreCase).TrimStart('\\', '/'))
            .Distinct()
            .ToList();

        if (exitCode != 0 && errors.Count == 0)
        {
            errors.Add($"dotnet build failed with exit code {exitCode}." + Environment.NewLine + output.Trim());
        }

        context.Report.BuildErrors.Clear();
        context.Report.BuildErrorCount = errors.Count;
        context.Report.BuildErrors.AddRange(errors);
        if (exitCode != 0) context.Report.Warning(Name, $"Build failed with exit code {exitCode}; see the build errors in the report.");
        context.Log.WriteLine(exitCode == 0 ? "    build succeeded" : $"    build failed with {errors.Count} errors");
    }

    private static (int ExitCode, string Output) RunBuild(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        using var process = Process.Start(start) ?? throw new MigrationException("Could not start dotnet build.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { } // Exited between HasExited and Kill.
            }
            process.WaitForExit();
            throw;
        }

        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    [GeneratedRegex(@"error [A-Z]+\d+", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLine();
}
