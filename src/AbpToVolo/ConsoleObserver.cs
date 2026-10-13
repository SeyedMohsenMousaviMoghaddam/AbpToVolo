using AbpToVolo.Core;

namespace AbpToVolo;

/// <summary>
/// Console front-end: a header with the settings, one line per step ("[3/19] Localization ... ✓ 1.2s, 5 changes"),
/// a live progress line for long steps, colored warnings/errors and a final summary.
/// </summary>
internal sealed class ConsoleObserver(bool verbose) : IMigrationObserver
{
    private readonly bool _interactive = !Console.IsOutputRedirected;
    private readonly Lock _lock = new();
    private DateTime _lastProgress = DateTime.MinValue;
    private bool _progressShown;

    public void RunStarted(MigrationOptions options, IReadOnlyList<string> steps)
    {
        Write(ConsoleColor.Cyan, "AbpToVolo — ASP.NET Boilerplate → ABP.io (Volo.Abp)");
        Write(ConsoleColor.Gray, $"  Source:      {options.InputPath}");
        Write(ConsoleColor.Gray, $"  Destination: {options.OutputPath}");
        Write(ConsoleColor.Gray, $"  ABP {options.AbpVersion} · {options.TargetFramework} · ABP.io names: {Yes(options.RenameLayers)} · Module Zero → ABP modules: {Yes(options.ConvertModuleZero)}");
        Write(ConsoleColor.Gray, $"  Contracts: {Yes(options.CreateContracts)} · Tests: {Yes(options.CreateTests)} · Host/DbMigrator: {Yes(options.StandardizeHost)} · Migrations: {options.Migrations} · Build: {Yes(options.Build)}");
        Console.WriteLine();
    }

    public void StepStarted(StepInfo step)
    {
        ClearProgress();
        Write(ConsoleColor.White, $"{step.Position} {step.Name} ...");
    }

    public void StepProgress(StepInfo step, int current, int total, string? item)
    {
        if (!_interactive || (DateTime.UtcNow - _lastProgress).TotalMilliseconds < 100 && current < total) return;
        lock (_lock)
        {
            _lastProgress = DateTime.UtcNow;
            var width = Math.Max(20, Math.Min(Console.WindowWidth, 140) - 1);
            const int barWidth = 24;
            var filled = total == 0 ? barWidth : (int)Math.Round(barWidth * (double)current / total);
            var text = $"      [{new string('█', filled)}{new string('░', barWidth - filled)}] {current}/{total} {item}";
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("\r" + (text.Length > width ? text[..width] : text.PadRight(width)));
            Console.ResetColor();
            _progressShown = true;
        }
    }

    public void StepCompleted(StepInfo step, StepSummary summary)
    {
        ClearProgress();
        var details = new List<string> { $"{summary.Duration.TotalSeconds:0.0}s" };
        if (summary.Changes > 0) details.Add($"{summary.Changes} changes");
        if (summary.Todos > 0) details.Add($"{summary.Todos} manual items");
        if (summary.Warnings > 0) details.Add($"{summary.Warnings} warnings");
        Write(summary.Warnings > 0 ? ConsoleColor.Yellow : ConsoleColor.Green, $"      ✓ {string.Join(", ", details)}");
    }

    public void StepSkipped(StepInfo step, string reason) => Write(ConsoleColor.DarkGray, $"{step.Position} {step.Name} — skipped ({reason})");

    public void StepFailed(StepInfo step, Exception exception)
    {
        ClearProgress();
        Write(ConsoleColor.Red, exception is OperationCanceledException ? $"      ✗ cancelled" : $"      ✗ {exception.Message}");
    }

    public void Log(LogLevel level, string message)
    {
        if (level == LogLevel.Detail && !verbose) return;
        ClearProgress();
        Write(level switch
        {
            LogLevel.Warning => ConsoleColor.Yellow,
            LogLevel.Error => ConsoleColor.Red,
            LogLevel.Success => ConsoleColor.Green,
            LogLevel.Detail => ConsoleColor.DarkGray,
            _ => ConsoleColor.Gray
        }, "      " + message);
    }

    public void Entry(ReportEntry entry)
    {
        if (entry.Level == ReportLevel.Warning)
        {
            ClearProgress();
            Write(ConsoleColor.Yellow, $"      ⚠ {entry.Message}{(entry.File is null ? "" : $" ({entry.File})")}");
        }
        else if (verbose && entry.Level is ReportLevel.Change or ReportLevel.Todo)
        {
            ClearProgress();
            Write(entry.Level == ReportLevel.Todo ? ConsoleColor.DarkYellow : ConsoleColor.DarkGray,
                $"      {(entry.Level == ReportLevel.Todo ? "TODO" : "·")} {entry.Message}{(entry.File is null ? "" : $" ({entry.File})")}");
        }
    }

    public void RunCompleted(MigrationReport report, TimeSpan duration, bool success)
    {
        ClearProgress();
        Console.WriteLine();
        if (!success)
        {
            Write(ConsoleColor.Red, $"Conversion stopped after {duration.TotalSeconds:0.0}s.");
            return;
        }

        var todos = report.Entries.Count(e => e.Level is ReportLevel.Todo or ReportLevel.Warning);
        Write(ConsoleColor.Green, $"Done in {duration.TotalSeconds:0.0}s: {report.CSharpFilesChanged} C# files and {report.ProjectsMigrated} projects converted, {todos} items need manual work.");
        if (report.BuildErrorCount is { } errors)
        {
            Write(errors == 0 ? ConsoleColor.Green : ConsoleColor.Yellow, errors == 0 ? "Build succeeded." : $"Build: {errors} errors (see the report).");
        }
    }

    private static string Yes(bool value) => value ? "yes" : "no";

    private void ClearProgress()
    {
        lock (_lock)
        {
            if (!_progressShown) return;
            Console.Write("\r" + new string(' ', Math.Max(20, Math.Min(Console.WindowWidth, 140) - 1)) + "\r");
            _progressShown = false;
        }
    }

    private void Write(ConsoleColor color, string text)
    {
        lock (_lock)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ResetColor();
        }
    }
}
