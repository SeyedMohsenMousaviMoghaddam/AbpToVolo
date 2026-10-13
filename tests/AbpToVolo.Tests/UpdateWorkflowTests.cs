using System.Security.Cryptography;
using AbpToVolo.Core;
using AbpToVolo.Steps;

namespace AbpToVolo.Tests;

/// <summary>
/// Git update workflow: converting the same source again into the same output gives exactly the same files
/// (generated migration ids, passphrase, solution GUIDs...), and .git / .abptovolo survive --force.
/// </summary>
public sealed class UpdateWorkflowTests : IDisposable
{
    private readonly string _output = Path.Combine(Path.GetTempPath(), "abptovolo-tests", Guid.NewGuid().ToString("N"));

    private static string SamplePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AbpToVolo.slnx")))
            {
                dir = dir.Parent;
            }

            return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Repository root not found"), "samples", "Acme.PhoneBook");
        }
    }

    private void Convert()
    {
        var steps = MigrationEngine.DefaultSteps()
            .Select(s => s is ResolveVersionsStep ? new ResolveVersionsStep(_ => null) : s)
            .Where(s => s is not (BuildVerificationStep or BuildFixStep or AbpIoModulesMigrationStep));
        new MigrationEngine(steps).Run(new MigrationOptions { InputPath = SamplePath, OutputPath = _output, Force = true }, TextWriter.Null);
    }

    private Dictionary<string, string> Hashes() =>
        Directory.EnumerateFiles(_output, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_output, f))
            .Where(f => !f.StartsWith(".git", StringComparison.Ordinal) && f is not (FileLogObserver.FileName or ReportWriterStep.JsonFile))
            .ToDictionary(f => f, f => System.Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_output, f)))));

    [Fact]
    public void Converting_the_same_source_again_gives_the_same_output()
    {
        Convert();
        var first = Hashes();
        Directory.CreateDirectory(Path.Combine(_output, ".git"));
        File.WriteAllText(Path.Combine(_output, ".git", "HEAD"), "ref: refs/heads/converted");

        Convert();
        var second = Hashes();

        Assert.True(File.Exists(Path.Combine(_output, ".git", "HEAD")), ".git must survive --force");
        Assert.True(File.Exists(MigrationState.PathIn(_output)));
        Assert.Empty(first.Keys.Except(second.Keys));
        Assert.Empty(second.Keys.Except(first.Keys));
        Assert.Empty(first.Where(f => second[f.Key] != f.Value).Select(f => f.Key));
        Assert.Contains("bin/", File.ReadAllText(Path.Combine(_output, ".gitignore")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_output, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
