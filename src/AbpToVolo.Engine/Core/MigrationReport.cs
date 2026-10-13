namespace AbpToVolo.Core;

public enum ReportLevel
{
    Info,
    Change,
    Todo,
    Warning
}

public sealed record ReportEntry(string Step, ReportLevel Level, string Message, string? File = null, int? Line = null);

public sealed class MigrationReport
{
    private readonly List<ReportEntry> _entries = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<ReportEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToList();
            }
        }
    }

    public List<string> BuildErrors { get; } = [];
    public int? BuildErrorCount { get; set; }
    public int CSharpFilesScanned { get; set; }
    public int CSharpFilesChanged { get; set; }
    public int ProjectsMigrated { get; set; }

    /// <summary>Free-form report sections (title → markdown lines), rendered after the summary.</summary>
    public List<(string Title, List<string> Lines)> Sections { get; } = [];

    public List<string> Section(string title)
    {
        lock (_lock)
        {
            var existing = Sections.FirstOrDefault(s => s.Title == title);
            if (existing.Lines is not null) return existing.Lines;
            var lines = new List<string>();
            Sections.Add((title, lines));
            return lines;
        }
    }

    /// <summary>Raised for every new entry (the engine forwards it to the observers).</summary>
    public event Action<ReportEntry>? EntryAdded;

    public int Count(ReportLevel level)
    {
        lock (_lock)
        {
            return _entries.Count(e => e.Level == level);
        }
    }

    public void Add(string step, ReportLevel level, string message, string? file = null, int? line = null)
    {
        var entry = new ReportEntry(step, level, message, file, line);
        lock (_lock)
        {
            _entries.Add(entry);
        }

        EntryAdded?.Invoke(entry);
    }

    public void Info(string step, string message, string? file = null) => Add(step, ReportLevel.Info, message, file);
    public void Change(string step, string message, string? file = null) => Add(step, ReportLevel.Change, message, file);
    public void Todo(string step, string message, string? file = null, int? line = null) => Add(step, ReportLevel.Todo, message, file, line);
    public void Warning(string step, string message, string? file = null) => Add(step, ReportLevel.Warning, message, file);
}
