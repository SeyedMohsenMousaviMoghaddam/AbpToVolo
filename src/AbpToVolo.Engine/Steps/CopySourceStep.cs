using AbpToVolo.Core;

namespace AbpToVolo.Steps;

/// <summary>Copies the Boilerplate solution to the output folder. The input folder is never modified.</summary>
public sealed class CopySourceStep : IMigrationStep
{
    internal const string StepName = "Copy source";
    public string Name => StepName;

    public void Execute(MigrationContext context)
    {
        var input = ResolveDirectoryPath(context.Options.InputPath);
        var output = ResolveDirectoryPath(context.Options.OutputPath);

        if (!Directory.Exists(input))
        {
            throw new MigrationException($"Input folder not found: {input}");
        }

        if (ContainsPath(input, output) || ContainsPath(output, input))
        {
            throw new MigrationException("The input and output folders must not be equal or contain one another.");
        }

        // Validate before --force removes anything. Links could lead back into the output
        // or create a recursive copy; do not silently follow or omit source files.
        ValidateSource(input, context.CancellationToken);
        context.CancellationToken.ThrowIfCancellationRequested();
        context.State = MigrationState.Load(output);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any(e => !KeptFolders.Contains(Path.GetFileName(e))))
        {
            if (!context.Options.Force)
            {
                throw new MigrationException($"Output folder is not empty: {output} (use --force to replace it).");
            }

            ClearOutput(output);
        }

        var count = CopyDirectory(input, output, context.CancellationToken);
        context.Report.Info(Name, $"Copied {count} files from {input}");

        var previous = context.State.SourceRevision;
        context.State.SourceRevision = GitRevision(input);
        if (context.State.IsUpdate)
        {
            context.Report.Info(Name, $"Re-conversion into an existing output (state of {MigrationState.Folder}/{MigrationState.FileName} kept" +
                                      (previous is not null ? $", previous source revision {previous}" : "") + ")");
        }
    }

    private static bool ContainsPath(string parent, string child)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(parent, child, comparison) ||
               child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, comparison);
    }

    private static string ResolveDirectoryPath(string path)
    {
        var full = Path.GetFullPath(path);
        var resolved = Path.GetPathRoot(full)!;
        foreach (var part in full[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, part);
            var directory = new DirectoryInfo(resolved);
            if (directory.LinkTarget is not null)
                resolved = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                           ?? throw new MigrationException($"Cannot resolve directory link: {resolved}");
        }

        return Path.TrimEndingDirectorySeparator(resolved);
    }

    private static void ValidateSource(string directory, CancellationToken cancellationToken)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is DirectoryInfo && FileSystem.IsIgnoredDirectory(entry.Name)) continue;
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new MigrationException($"Source contains a symbolic link or reparse point: {entry.FullName}. Use a source copy with regular files and directories.");
            if (entry is DirectoryInfo) ValidateSource(entry.FullName, cancellationToken);
        }
    }

    /// <summary>
    /// Kept by --force: ".git" (the output is a repository: git workflow for updates), ".abptovolo" (state of earlier
    /// conversions) and ".vs" (Visual Studio state, locked while the converted solution is open in the IDE).
    /// </summary>
    private static readonly HashSet<string> KeptFolders = new(StringComparer.OrdinalIgnoreCase) { ".git", MigrationState.Folder, MigrationState.LegacyFolder, ".vs" };

    private static string? GitRevision(string folder)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("git", "rev-parse HEAD")
            {
                WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(10000);
            return process.ExitCode == 0 && output.Length >= 7 ? output : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null; // git is not installed
        }
    }

    /// <summary>--force: empties the output folder, except the folders of <see cref="KeptFolders"/>. Locked files stop
    /// the run with a clear message instead of leaving half a copy.</summary>
    private static void ClearOutput(string output)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(output))
            {
                if (!KeptFolders.Contains(Path.GetFileName(dir)))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }

            foreach (var file in Directory.EnumerateFiles(output).Where(f => !KeptFolders.Contains(Path.GetFileName(f))))
            {
                File.Delete(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new MigrationException($"Could not replace the output folder {output}: {e.Message} " +
                                         "Close the programs that use it (Visual Studio, a running host, tests) and run again.");
        }
    }

    private static int CopyDirectory(string source, string target, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(target);
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            count++;
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(dir);
            if (!FileSystem.IsIgnoredDirectory(name))
            {
                count += CopyDirectory(dir, Path.Combine(target, name), cancellationToken);
            }
        }

        return count;
    }
}

public sealed class MigrationException(string message) : Exception(message);
