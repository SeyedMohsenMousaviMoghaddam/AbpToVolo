using System.Diagnostics;
using System.Text.RegularExpressions;
using AbpToVolo.Core;

namespace AbpToVolo.Steps;

/// <summary>Optional (--build): runs "dotnet build" on the converted solution and records the compiler errors.</summary>
public sealed partial class BuildVerificationStep : IMigrationStep
{
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

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = stdout.Result + stderr.Result;

        var errors = output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => ErrorLine().IsMatch(l))
            .Select(l => l.Replace(context.OutputRoot, "", StringComparison.OrdinalIgnoreCase).TrimStart('\\', '/'))
            .Distinct()
            .ToList();

        context.Report.BuildErrorCount = errors.Count;
        context.Report.BuildErrors.AddRange(errors);
        context.Log.WriteLine(process.ExitCode == 0 ? "    build succeeded" : $"    build failed with {errors.Count} errors");
    }

    [GeneratedRegex(@"error [A-Z]+\d+", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLine();
}
