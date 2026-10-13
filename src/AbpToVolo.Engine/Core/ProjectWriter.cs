using System.Diagnostics;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AbpToVolo.Core;

/// <summary>Creates project files, adds project references and registers projects in the solution file.</summary>
public static class ProjectWriter
{
    public static void Save(XDocument doc, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = true, Encoding = new UTF8Encoding(false) };
        using (var writer = XmlWriter.Create(path, settings))
        {
            doc.Save(writer);
        }

        File.AppendAllText(path, Environment.NewLine);
    }

    /// <summary>A minimal SDK-style project; packages are added later by the project file step.</summary>
    public static string CreateProject(string directory, string name, string targetFramework, string rootNamespace,
        IEnumerable<string> projectReferences, IEnumerable<(string Name, string Version)>? packages = null,
        string sdk = "Microsoft.NET.Sdk", IDictionary<string, string>? properties = null)
    {
        var path = Path.Combine(directory, name + ".csproj");
        var propertyGroup = new XElement("PropertyGroup",
            new XElement("TargetFramework", targetFramework),
            new XElement("RootNamespace", rootNamespace));
        foreach (var (key, value) in properties ?? new Dictionary<string, string>())
        {
            propertyGroup.Add(new XElement(key, value));
        }

        var root = new XElement("Project", new XAttribute("Sdk", sdk), propertyGroup);

        var packageList = packages?.ToList() ?? [];
        if (packageList.Count > 0)
        {
            root.Add(new XElement("ItemGroup", packageList.Select(p =>
                new XElement("PackageReference", new XAttribute("Include", p.Name), new XAttribute("Version", p.Version)))));
        }

        var references = projectReferences.ToList();
        if (references.Count > 0)
        {
            root.Add(new XElement("ItemGroup", references.Select(r =>
                new XElement("ProjectReference", new XAttribute("Include", Path.GetRelativePath(directory, r))))));
        }

        Save(new XDocument(root), path);
        return path;
    }

    public static bool AddProjectReference(string csproj, string referencedCsproj)
    {
        var doc = XDocument.Load(csproj);
        var relative = Path.GetRelativePath(Path.GetDirectoryName(csproj)!, referencedCsproj);
        var fileName = Path.GetFileName(referencedCsproj);
        if (doc.Descendants("ProjectReference").Any(r => (r.Attribute("Include")?.Value ?? "").Replace('/', '\\').EndsWith(fileName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var group = doc.Root!.Elements("ItemGroup").FirstOrDefault(g => g.Elements("ProjectReference").Any());
        if (group is null)
        {
            group = new XElement("ItemGroup");
            doc.Root.Add(group);
        }

        group.Add(new XElement("ProjectReference", new XAttribute("Include", relative)));
        Save(doc, csproj);
        return true;
    }

    public static bool AddPackageReference(string csproj, string package, string version)
    {
        var doc = XDocument.Load(csproj);
        if (doc.Descendants("PackageReference").Any(r => string.Equals(r.Attribute("Include")?.Value, package, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var group = doc.Root!.Elements("ItemGroup").FirstOrDefault(g => g.Elements("PackageReference").Any());
        if (group is null)
        {
            group = new XElement("ItemGroup");
            doc.Root.Add(group);
        }

        group.Add(new XElement("PackageReference", new XAttribute("Include", package), new XAttribute("Version", version)));
        Save(doc, csproj);
        return true;
    }

    /// <summary>Runs "dotnet sln add"; returns an error message or null.</summary>
    public static string? AddToSolution(string solution, string csproj, string? solutionFolder = null)
    {
        var args = new List<string> { "sln", solution, "add", csproj };
        if (solutionFolder is not null)
        {
            args.Add("--solution-folder");
            args.Add(solutionFolder);
        }

        try
        {
            var before = solution.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ? File.ReadAllText(solution) : null;
            var start = new ProcessStartInfo("dotnet", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(solution)!
            };
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) return output.Trim();

            if (before is not null) MakeNewProjectGuidsStable(solution, before);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex SlnProject =
        new(@"^Project\(""\{[0-9A-F-]+\}""\) = ""(?<name>[^""]+)"", ""(?<path>[^""]+)"", ""\{(?<guid>[0-9A-Fa-f-]+)\}""", System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>
    /// "dotnet sln add" gives the added projects (and solution folders) random GUIDs; they are replaced by GUIDs derived
    /// from the project path, so converting again produces the same .sln (git workflow: no spurious changes).
    /// The GUIDs of the projects the solution already had are kept.
    /// </summary>
    private static void MakeNewProjectGuidsStable(string solution, string before)
    {
        var existing = SlnProject.Matches(before).Select(m => m.Groups["guid"].Value.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        var text = FileSystem.ReadText(solution).Text;
        foreach (System.Text.RegularExpressions.Match match in SlnProject.Matches(text))
        {
            var guid = match.Groups["guid"].Value.ToUpperInvariant();
            if (existing.Contains(guid)) continue;

            var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(match.Groups["path"].Value.Replace('/', '\\').ToUpperInvariant()));
            var stable = new Guid(hash).ToString("D").ToUpperInvariant();
            text = System.Text.RegularExpressions.Regex.Replace(text, System.Text.RegularExpressions.Regex.Escape(match.Groups["guid"].Value), stable,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        FileSystem.WriteText(solution, text, FileSystem.ReadText(solution).Encoding);
    }

    public static string? FindSolution(string root) =>
        FileSystem.EnumerateFiles(root, "*.sln").Concat(FileSystem.EnumerateFiles(root, "*.slnx")).OrderBy(f => f.Length).FirstOrDefault();

    /// <summary>Copies simple compile settings (Nullable, ImplicitUsings) so moved code compiles the same way.</summary>
    public static Dictionary<string, string> CompileSettings(string csproj)
    {
        var doc = XDocument.Load(csproj);
        return new[] { "Nullable", "ImplicitUsings" }
            .Select(n => (Name: n, Value: doc.Descendants(n).FirstOrDefault()?.Value))
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .ToDictionary(x => x.Name, x => x.Value!);
    }
}
