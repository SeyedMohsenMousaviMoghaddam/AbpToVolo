using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AbpToVolo.Core;

public sealed record BuildDiagnostic(string File, int Line, int Column, string Code, string Message, string Project);

/// <summary>Runs "dotnet build" and parses the compiler errors (file, line, column, code, message, project).</summary>
public static partial class DotnetBuild
{
    public sealed record Result(int ExitCode, List<BuildDiagnostic> Errors, List<string> OtherErrors);

    public static Result Run(string target, string workingDirectory, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo("dotnet", ["build", target, "-nologo", "-v:q", "-clp:NoSummary", "-p:TreatWarningsAsErrors=false"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory
        };

        using var process = Process.Start(start)!;
        using var registration = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
        });
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        cancellationToken.ThrowIfCancellationRequested();

        var errors = new List<BuildDiagnostic>();
        var other = new List<string>();
        foreach (var line in (stdout.Result + "\n" + stderr.Result).Split('\n').Select(l => l.Trim()).Distinct())
        {
            if (Diagnostic().Match(line) is { Success: true } m)
            {
                errors.Add(new BuildDiagnostic(m.Groups["file"].Value, int.Parse(m.Groups["line"].Value), int.Parse(m.Groups["col"].Value),
                    m.Groups["code"].Value, m.Groups["message"].Value.Trim(), Path.GetFileNameWithoutExtension(m.Groups["project"].Value)));
            }
            else if (line.Contains(": error ", StringComparison.Ordinal))
            {
                other.Add(line);
            }
        }

        return new Result(process.ExitCode, errors.DistinctBy(e => (e.File, e.Line, e.Column, e.Code)).ToList(), other);
    }

    [GeneratedRegex(@"^(?<file>[^\s].*?\.cs)\((?<line>\d+),(?<col>\d+)\): error (?<code>CS\d+): (?<message>.*?)\s*\[(?<project>[^\]]+\.csproj)\]$")]
    private static partial Regex Diagnostic();
}
