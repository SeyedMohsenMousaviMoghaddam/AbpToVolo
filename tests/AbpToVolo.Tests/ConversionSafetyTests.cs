using AbpToVolo.Core;
using AbpToVolo.Steps;

namespace AbpToVolo.Tests;

public sealed class ConversionSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "abptovolo-safety-" + Guid.NewGuid().ToString("N"));

    private MigrationContext Context(string input, string output, CancellationToken token = default) =>
        new(new MigrationOptions { InputPath = input, OutputPath = output, Force = true, Build = true },
            new MigrationReport(), new TextWriterObserver(TextWriter.Null), token);

    [Theory]
    [InlineData("same")]
    [InlineData("child")]
    [InlineData("parent")]
    [InlineData("trailing-separator")]
    public void Overlapping_paths_are_rejected_before_deleting_any_files(string kind)
    {
        var input = Path.Combine(_root, "source");
        Directory.CreateDirectory(input);
        var marker = Path.Combine(input, "important.cs");
        File.WriteAllText(marker, "original");
        var output = kind switch
        {
            "child" => Path.Combine(input, "output"),
            "parent" => _root,
            _ => input
        };
        if (kind == "trailing-separator") input += Path.DirectorySeparatorChar;

        Assert.Throws<MigrationException>(() => new CopySourceStep().Execute(Context(input, output)));
        Assert.Equal("original", File.ReadAllText(marker));
    }

    [Fact]
    public void Sibling_with_the_same_prefix_is_allowed_and_ignored_folders_are_not_copied()
    {
        var input = Path.Combine(_root, "source");
        var output = input + "-converted";
        Directory.CreateDirectory(Path.Combine(input, "bin"));
        File.WriteAllText(Path.Combine(input, "bin", "build.dll"), "build");
        File.WriteAllText(Path.Combine(input, "App.cs"), "source");
        new CopySourceStep().Execute(Context(input, output));
        Assert.Equal("source", File.ReadAllText(Path.Combine(output, "App.cs")));
        Assert.False(Directory.Exists(Path.Combine(output, "bin")));
    }

    [Fact]
    public void Output_alias_to_a_source_ancestor_is_rejected()
    {
        if (OperatingSystem.IsWindows()) return; // Creating Windows links can require elevation.
        var input = Path.Combine(_root, "source");
        Directory.CreateDirectory(input);
        var alias = Path.Combine(_root, "alias");
        Directory.CreateSymbolicLink(alias, _root);
        File.WriteAllText(Path.Combine(input, "App.cs"), "original");
        try
        {
            Assert.Throws<MigrationException>(() => new CopySourceStep().Execute(Context(input, alias)));
            Assert.Equal("original", File.ReadAllText(Path.Combine(input, "App.cs")));
        }
        finally { Directory.Delete(alias); }
    }

    [Fact]
    public void Source_links_are_rejected_before_clearing_the_destination()
    {
        if (OperatingSystem.IsWindows()) return;
        var input = Path.Combine(_root, "source");
        var output = Path.Combine(_root, "output");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "keep.txt"), "keep");
        Directory.CreateSymbolicLink(Path.Combine(input, "loop"), input);
        try
        {
            Assert.Throws<MigrationException>(() => new CopySourceStep().Execute(Context(input, output)));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(output, "keep.txt")));
        }
        finally { Directory.Delete(Path.Combine(input, "loop")); }
    }

    [Fact]
    public void Cancelled_copy_does_not_clear_the_destination()
    {
        var input = Path.Combine(_root, "source");
        var output = Path.Combine(_root, "output");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "keep.txt"), "keep");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new CopySourceStep().Execute(Context(input, output, cancellation.Token)));
        Assert.True(File.Exists(Path.Combine(output, "keep.txt")));
    }

    [Fact]
    public void Failed_copy_does_not_overwrite_a_log_in_the_source()
    {
        Directory.CreateDirectory(_root);
        var log = Path.Combine(_root, FileLogObserver.FileName);
        File.WriteAllText(log, "source log");
        using var observer = new FileLogObserver();
        Assert.Throws<MigrationException>(() => new MigrationEngine([new CopySourceStep()])
            .Run(Context(_root, _root).Options, observer));
        Assert.Equal("source log", File.ReadAllText(log));
        Assert.NotEqual(log, observer.LogFilePath);
    }

    [Theory]
    [InlineData(0, "Build succeeded.", 0)]
    [InlineData(1, "SDK resolver failed without a diagnostic code", 1)]
    [InlineData(1, "App.cs(1,1): error CS0103: missing name\nApp.cs(1,1): error CS0103: missing name", 1)]
    public void Build_failures_are_reported_and_mark_the_run_unsuccessful(int exitCode, string output, int errors)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "App.sln"), "");
        var observer = new CompletionObserver();
        var engine = new MigrationEngine([new BuildVerificationStep((_, _) => (exitCode, output)), new ReportWriterStep()]);
        var report = engine.Run(Context("unused", _root).Options, observer);
        Assert.Equal(errors, report.BuildErrorCount);
        Assert.Equal(errors, report.BuildErrors.Count);
        Assert.Equal(exitCode == 0, observer.Success);
        Assert.Contains($"| Build errors | {errors} |", File.ReadAllText(Path.Combine(_root, ReportWriterStep.MarkdownFile)));
        if (exitCode != 0) Assert.Contains(report.BuildErrors, e => e.Contains(output.Split('\n')[0]));
    }

    private sealed class CompletionObserver : IMigrationObserver
    {
        public bool? Success { get; private set; }
        public void RunCompleted(MigrationReport report, TimeSpan duration, bool success) => Success = success;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
