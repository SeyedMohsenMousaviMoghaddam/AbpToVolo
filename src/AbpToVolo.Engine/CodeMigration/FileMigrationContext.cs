using AbpToVolo.Core;

namespace AbpToVolo.CodeMigration;

/// <summary>State shared by all rewriters while one C# file is being converted.</summary>
public sealed class FileMigrationContext
{
    public FileMigrationContext(string filePath, string newLine, ProjectInfo? project = null, LocalizationResourceInfo? localization = null)
    {
        FilePath = filePath;
        NewLine = newLine;
        Project = project;
        Localization = localization;
    }

    public string FilePath { get; }
    public string NewLine { get; }
    public ProjectInfo? Project { get; }
    public LocalizationResourceInfo? Localization { get; }

    /// <summary>Namespaces the rewritten code needs; added by <see cref="Rewriters.UsingsRewriter"/>.</summary>
    public SortedSet<string> RequiredUsings { get; } = new(StringComparer.Ordinal);

    /// <summary>Human readable list of what was changed (goes to the report).</summary>
    public List<string> Changes { get; } = [];

    /// <summary>Problems that need a developer; written as a comment block on top of the file and into the report.</summary>
    public List<string> FileTodos { get; } = [];

    /// <summary>True when the file contains Module Zero types (users, roles, tenants...).</summary>
    public bool UsesZero { get; set; }

    public void RequireUsing(string? ns)
    {
        if (!string.IsNullOrWhiteSpace(ns)) RequiredUsings.Add(ns);
    }

    public void Change(string message)
    {
        if (!Changes.Contains(message)) Changes.Add(message);
    }

    public void FileTodo(string message)
    {
        if (!FileTodos.Contains(message)) FileTodos.Add(message);
    }
}
