using System.Text;

namespace AbpToVolo.Core;

/// <summary>
/// Writes a detailed, timestamped log (every step, change, TODO and warning) to abp-migration.log in the output
/// folder. The output folder may be (re)created by the first step, so lines are buffered until it exists.
/// </summary>
public sealed class FileLogObserver : IMigrationObserver, IDisposable
{
    public const string FileName = "abp-migration.log";

    private readonly List<string> _buffer = [];
    private readonly Lock _lock = new();
    private StreamWriter? _writer;
    private string? _outputPath;

    public string? LogFilePath { get; private set; }

    public void RunStarted(MigrationOptions options, IReadOnlyList<string> steps)
    {
        _outputPath = options.OutputPath;
        Write("INFO", $"AbpToVolo run started");
        Write("INFO", $"Source:      {options.InputPath}");
        Write("INFO", $"Destination: {options.OutputPath}");
        Write("INFO", $"ABP {options.AbpVersion}, {options.TargetFramework}, migrations={options.Migrations}, contracts={options.CreateContracts}, " +
                      $"tests={options.CreateTests}, abpLayerNames={options.RenameLayers}, zero={options.ConvertModuleZero}, build={options.Build}");
        Write("INFO", $"Steps: {string.Join(" → ", steps)}");
    }

    public void StepStarted(StepInfo step) => Write("STEP", $"{step.Position} {step.Name} started");

    public void StepCompleted(StepInfo step, StepSummary summary)
    {
        Write("STEP", $"{step.Position} {step.Name} completed in {summary.Duration.TotalSeconds:0.0}s " +
                      $"({summary.Changes} changes, {summary.Todos} manual items, {summary.Warnings} warnings)");
        OpenIfPossible();
    }

    public void StepSkipped(StepInfo step, string reason) => Write("STEP", $"{step.Position} {step.Name} skipped: {reason}");

    public void StepFailed(StepInfo step, Exception exception) => Write("ERROR", $"{step.Position} {step.Name} failed: {exception}");

    public void Log(LogLevel level, string message) => Write(level switch
    {
        LogLevel.Detail => "DEBUG",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        _ => "INFO"
    }, message);

    public void Entry(ReportEntry entry)
    {
        var location = entry.File is null ? "" : entry.Line is null ? $" [{entry.File}]" : $" [{entry.File}:{entry.Line}]";
        Write(entry.Level switch
        {
            ReportLevel.Todo => "TODO",
            ReportLevel.Warning => "WARN",
            ReportLevel.Change => "CHANGE",
            _ => "INFO"
        }, $"{entry.Step}: {entry.Message}{location}");
    }

    public void RunCompleted(MigrationReport report, TimeSpan duration, bool success)
    {
        Write(success ? "INFO" : "ERROR", $"Run {(success ? "completed" : "FAILED")} in {duration.TotalSeconds:0.0}s: " +
                                          $"{report.CSharpFilesChanged} C# files changed, {report.ProjectsMigrated} projects");
        OpenIfPossible(fallbackToTemp: true);
        Dispose();
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-6}] {message}";
        lock (_lock)
        {
            if (_writer is null)
            {
                _buffer.Add(line);
            }
            else
            {
                _writer.WriteLine(line);
            }
        }
    }

    private void OpenIfPossible(bool fallbackToTemp = false)
    {
        lock (_lock)
        {
            if (_writer is not null) return;
            var folder = _outputPath is not null && Directory.Exists(_outputPath) ? _outputPath : fallbackToTemp ? Path.GetTempPath() : null;
            if (folder is null) return;

            LogFilePath = Path.Combine(folder, FileName);
            _writer = new StreamWriter(LogFilePath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
            foreach (var line in _buffer) _writer.WriteLine(line);
            _buffer.Clear();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
