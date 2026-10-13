using AbpToVolo.Core;

namespace AbpToVolo.Steps;

/// <summary>Copies the Boilerplate solution to the output folder. The input folder is never modified.</summary>
public sealed class CopySourceStep : IMigrationStep
{
    public string Name => "Copy source";

    public void Execute(MigrationContext context)
    {
        var input = Path.GetFullPath(context.Options.InputPath);
        var output = Path.GetFullPath(context.Options.OutputPath);

        if (!Directory.Exists(input))
        {
            throw new MigrationException($"Input folder not found: {input}");
        }

        if (output.StartsWith(input + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
        {
            throw new MigrationException("The output folder must not be the input folder or inside it.");
        }

        context.State = MigrationState.Load(output);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any(e => !KeptFolders.Contains(Path.GetFileName(e))))
        {
            if (!context.Options.Force)
            {
                throw new MigrationException($"Output folder is not empty: {output} (use --force to replace it).");
            }

            ClearOutput(output);
        }

        var count = CopyDirectory(input, output);
        context.Report.Info(Name, $"Copied {count} files from {input}");

        var previous = context.State.SourceRevision;
        context.State.SourceRevision = GitRevision(input);
        if (context.State.IsUpdate)
        {
            context.Report.Info(Name, $"Re-conversion into an existing output (state of {MigrationState.Folder}/{MigrationState.FileName} kept" +
                                      (previous is not null ? $", previous source revision {previous}" : "") + ")");
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

    private static int CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            count++;
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (!FileSystem.IsIgnoredDirectory(name))
            {
                count += CopyDirectory(dir, Path.Combine(target, name));
            }
        }

        return count;
    }
}

public sealed class MigrationException(string message) : Exception(message);
