using System.Text;

namespace AbpToVolo.Core;

public static class FileSystem
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".vs", ".idea", ".git", "node_modules", "packages", "TestResults", LegacyFolder
    };

    /// <summary>Boilerplate-only code that has no ABP.io equivalent is moved here and excluded from compilation.</summary>
    public const string LegacyFolder = "_BoilerplateLegacy";

    public static bool IsIgnoredDirectory(string name) => IgnoredDirectories.Contains(name);

    /// <summary>Enumerates files recursively while skipping build output and tooling folders.</summary>
    public static IEnumerable<string> EnumerateFiles(string root, string pattern)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(dir, pattern))
            {
                yield return file;
            }

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (!IsIgnoredDirectory(Path.GetFileName(sub)))
                {
                    pending.Push(sub);
                }
            }
        }
    }

    public static (string Text, Encoding Encoding) ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var encoding = new UTF8Encoding(hasBom);
        var text = hasBom ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) : Encoding.UTF8.GetString(bytes);
        return (text, encoding);
    }

    public static void WriteText(string path, string text, Encoding? encoding = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
    }

    public static string DetectNewLine(string text) => text.Contains("\r\n") ? "\r\n" : "\n";
}
