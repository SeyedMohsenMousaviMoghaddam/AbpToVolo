namespace AbpToVolo.Core;

public enum LogLevel
{
    Detail,
    Info,
    Success,
    Warning,
    Error
}

public sealed record StepInfo(int Index, int Count, string Name)
{
    public string Position => $"[{Index + 1}/{Count}]";
}

public sealed record StepSummary(TimeSpan Duration, int Changes, int Todos, int Warnings);

/// <summary>
/// Receives everything that happens during a conversion. All members have empty default implementations, so a
/// front-end (console, desktop app, log file) only overrides what it shows. Calls come from the conversion thread.
/// </summary>
public interface IMigrationObserver
{
    void RunStarted(MigrationOptions options, IReadOnlyList<string> steps) { }

    void StepStarted(StepInfo step) { }

    /// <summary>Progress inside a long step (e.g. "file 120 of 2898").</summary>
    void StepProgress(StepInfo step, int current, int total, string? item) { }

    void StepCompleted(StepInfo step, StepSummary summary) { }

    void StepSkipped(StepInfo step, string reason) { }

    void StepFailed(StepInfo step, Exception exception) { }

    void Log(LogLevel level, string message) { }

    /// <summary>Every change / TODO / warning recorded in the report, as it happens.</summary>
    void Entry(ReportEntry entry) { }

    void RunCompleted(MigrationReport report, TimeSpan duration, bool success) { }
}

public sealed class CompositeObserver(params IMigrationObserver[] observers) : IMigrationObserver
{
    private readonly IMigrationObserver[] _observers = observers.Where(o => o is not null).ToArray();

    public void RunStarted(MigrationOptions options, IReadOnlyList<string> steps) => Each(o => o.RunStarted(options, steps));
    public void StepStarted(StepInfo step) => Each(o => o.StepStarted(step));
    public void StepProgress(StepInfo step, int current, int total, string? item) => Each(o => o.StepProgress(step, current, total, item));
    public void StepCompleted(StepInfo step, StepSummary summary) => Each(o => o.StepCompleted(step, summary));
    public void StepSkipped(StepInfo step, string reason) => Each(o => o.StepSkipped(step, reason));
    public void StepFailed(StepInfo step, Exception exception) => Each(o => o.StepFailed(step, exception));
    public void Log(LogLevel level, string message) => Each(o => o.Log(level, message));
    public void Entry(ReportEntry entry) => Each(o => o.Entry(entry));
    public void RunCompleted(MigrationReport report, TimeSpan duration, bool success) => Each(o => o.RunCompleted(report, duration, success));

    private void Each(Action<IMigrationObserver> action)
    {
        foreach (var observer in _observers)
        {
            try
            {
                action(observer);
            }
            catch
            {
                // A broken front-end must never stop the conversion.
            }
        }
    }
}

/// <summary>Plain text output (used by the old TextWriter API and by tests).</summary>
public sealed class TextWriterObserver(TextWriter writer) : IMigrationObserver
{
    public void StepStarted(StepInfo step) => writer.WriteLine($"> {step.Name}");
    public void Log(LogLevel level, string message)
    {
        if (level != LogLevel.Detail) writer.WriteLine("    " + message);
    }
}

/// <summary>Forwards the lines that steps write to <see cref="MigrationContext.Log"/> to the observer.</summary>
internal sealed class ObserverTextWriter(IMigrationObserver observer) : TextWriter
{
    private readonly System.Text.StringBuilder _line = new();

    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

    public override void Write(char value)
    {
        if (value == '\n')
        {
            Flush();
        }
        else if (value != '\r')
        {
            _line.Append(value);
        }
    }

    public override void Flush()
    {
        if (_line.Length == 0) return;
        observer.Log(LogLevel.Info, _line.ToString().Trim());
        _line.Clear();
    }
}
