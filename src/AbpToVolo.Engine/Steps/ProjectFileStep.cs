using System.Text;
using System.Xml;
using System.Xml.Linq;
using AbpToVolo.Core;
using AbpToVolo.Mappings;

namespace AbpToVolo.Steps;

/// <summary>
/// Updates every .csproj: target framework, Abp.* → Volo.Abp.* packages, removal of Castle packages,
/// runtime-aligned Microsoft packages, layer-specific ABP.io packages and embedded JSON localization.
/// Supports central package management (Directory.Packages.props).
/// </summary>
public sealed class ProjectFileStep : IMigrationStep
{
    private const string StepName = "Project files";

    public string Name => StepName;

    public void Execute(MigrationContext context)
    {
        var central = CentralPackages.Find(context.OutputRoot);

        foreach (var project in context.Projects)
        {
            try
            {
                MigrateProject(context, project, central);
                context.Report.ProjectsMigrated++;
            }
            catch (Exception ex)
            {
                context.Report.Warning(Name, $"Project file not migrated: {ex.Message}", context.Relative(project.CsprojPath));
            }
        }

        central?.Save(context);
    }

    private void MigrateProject(MigrationContext context, ProjectInfo project, CentralPackages? central)
    {
        var relative = context.Relative(project.CsprojPath);
        var doc = XDocument.Load(project.CsprojPath);
        var root = doc.Root!;
        if (root.Attribute("Sdk") is null)
        {
            context.Report.Todo(Name, "Old-style (non-SDK) project file: convert it to an SDK-style project manually.", relative);
            return;
        }

        var options = context.Options;
        SetTargetFramework(context, root, relative);

        var packagesToAdd = new List<string>();
        var references = root.Descendants("PackageReference").ToList();
        foreach (var reference in references)
        {
            var package = reference.Attribute("Include")?.Value ?? reference.Attribute("Update")?.Value;
            if (package is null) continue;

            if (PackageMappings.IsBoilerplatePackage(package))
            {
                reference.Remove();
                if (PackageMappings.Map.TryGetValue(package, out var targets))
                {
                    packagesToAdd.AddRange(targets);
                    context.Report.Change(Name, targets.Length == 0
                        ? $"Removed {package} (no ABP.io counterpart)"
                        : $"{package} → {string.Join(", ", targets)}", relative);
                    if (package.Contains("Zero", StringComparison.OrdinalIgnoreCase)) context.ProjectsUsingZero.Add(project.Name);
                }
                else
                {
                    context.Report.Todo(Name, $"Package {package} has no known ABP.io replacement and was removed.", relative);
                }
            }
            else if (PackageMappings.IsObsoleteThirdParty(package))
            {
                reference.Remove();
                context.Report.Change(Name, $"Removed {package} (Castle Windsor infrastructure)", relative);
            }
            else if (PackageMappings.IsRuntimeAlignedMicrosoftPackage(package))
            {
                BumpVersion(context, reference, package, context.MicrosoftPackagesVersion, relative, central);
            }
        }

        packagesToAdd.AddRange(PackageMappings.LayerPackages(project.Layer, context.ProjectsUsingZero.Contains(project.Name)));

        var localization = context.LocalizationResources.Where(l => l.ProjectName == project.Name).ToList();
        if (localization.Count > 0)
        {
            AddEmbeddedLocalization(root, localization);
            packagesToAdd.Add("Microsoft.Extensions.FileProviders.Embedded");
            context.Report.Change(Name, "Embedded JSON localization files (GenerateEmbeddedFilesManifest)", relative);
        }

        var existing = root.Descendants("PackageReference")
            .Select(r => r.Attribute("Include")?.Value)
            .Where(v => v is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Packages/frameworks the code needs (Boilerplate packages used to bring them transitively).
        var usedNamespaces = UsedNamespaces(context, project);
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ns, package, version) in PackageMappings.NamespacePackages)
        {
            if (!usedNamespaces.Any(u => u == ns || u.StartsWith(ns + ".", StringComparison.Ordinal))) continue;
            var family = package.EndsWith(".Domain", StringComparison.Ordinal) ? package[..^".Domain".Length] : package;
            if (existing.Concat(packagesToAdd).Any(p => p.Equals(package, StringComparison.OrdinalIgnoreCase) || p.StartsWith(family + ".", StringComparison.OrdinalIgnoreCase))) continue;
            packagesToAdd.Add(package);
            if (version is not null) versions[package] = version;
        }

        if (root.Attribute("Sdk")?.Value != "Microsoft.NET.Sdk.Web" &&
            usedNamespaces.Any(u => u.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) && u is not ("Microsoft.AspNetCore.Authorization" or "Microsoft.AspNetCore.Identity")) &&
            !root.Descendants("FrameworkReference").Any(f => f.Attribute("Include")?.Value == "Microsoft.AspNetCore.App"))
        {
            root.Add(new XElement("ItemGroup", new XElement("FrameworkReference", new XAttribute("Include", "Microsoft.AspNetCore.App"))));
            context.Report.Change(StepName, "Added FrameworkReference Microsoft.AspNetCore.App (the code uses ASP.NET Core types)", relative);
        }

        var itemGroup = root.Elements("ItemGroup").FirstOrDefault(g => g.Elements("PackageReference").Any());
        if (itemGroup is null)
        {
            itemGroup = new XElement("ItemGroup");
            root.Add(itemGroup);
        }

        foreach (var package in packagesToAdd.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!existing.Add(package)) continue;

            var version = versions.TryGetValue(package, out var fixedVersion) ? fixedVersion
                : package.StartsWith("Volo.Abp", StringComparison.Ordinal) ? options.AbpVersion : context.MicrosoftPackagesVersion;
            var element = new XElement("PackageReference", new XAttribute("Include", package));
            if (central is null)
            {
                element.Add(new XAttribute("Version", version));
            }
            else
            {
                central.Ensure(package, version);
            }

            itemGroup.Add(element);
            context.Report.Change(Name, $"Added package {package}", relative);
        }

        // Drop item groups that became empty after removing Boilerplate packages.
        foreach (var group in root.Elements("ItemGroup").Where(g => !g.HasElements && !g.HasAttributes).ToList())
        {
            group.Remove();
        }

        Save(doc, project.CsprojPath);
    }

    private static readonly System.Text.RegularExpressions.Regex UsingPattern =
        new(@"^\s*(?:global\s+)?using\s+(?:static\s+)?([A-Za-z_][\w\.]*)\s*;", System.Text.RegularExpressions.RegexOptions.Multiline);

    private static HashSet<string> UsedNamespaces(MigrationContext context, ProjectInfo project) =>
        FileSystem.EnumerateFiles(project.Directory, "*.cs")
            .Where(f => !context.IsExcluded(f))
            .SelectMany(f => UsingPattern.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

    private static void SetTargetFramework(MigrationContext context, XElement root, string relative)
    {
        var target = context.Options.TargetFramework;
        var single = root.Descendants("TargetFramework").FirstOrDefault();
        var multi = root.Descendants("TargetFrameworks").FirstOrDefault();

        if (multi is not null)
        {
            context.Report.Change(StepName, $"TargetFrameworks {multi.Value} → TargetFramework {target}", relative);
            multi.ReplaceWith(new XElement("TargetFramework", target));
        }
        else if (single is not null && single.Value != target)
        {
            context.Report.Change(StepName, $"TargetFramework {single.Value} → {target}", relative);
            single.Value = target;
        }

        // Settings that only made sense for old .NET Core / .NET Framework targets.
        foreach (var obsolete in new[] { "AssetTargetFallback", "PackageTargetFallback", "RuntimeFrameworkVersion", "NetStandardImplicitPackageVersion" })
        {
            root.Descendants(obsolete).ToList().ForEach(e => e.Remove());
        }

        // An old pinned C# version (e.g. 7.2) cannot compile ABP.io code (records, file-scoped namespaces, ...).
        foreach (var langVersion in root.Descendants("LangVersion").Where(e => decimal.TryParse(e.Value, System.Globalization.CultureInfo.InvariantCulture, out _)).ToList())
        {
            context.Report.Change(StepName, $"Removed LangVersion {langVersion.Value} (the SDK default for {target} is used)", relative);
            langVersion.Remove();
        }
    }

    private static void BumpVersion(MigrationContext context, XElement reference, string package, string version, string relative, CentralPackages? central)
    {
        var attribute = reference.Attribute("Version");
        var current = attribute?.Value ?? reference.Element("Version")?.Value;
        if (current is null)
        {
            central?.Bump(package, version);
            return;
        }

        if (IsLower(current, version))
        {
            if (attribute is not null) attribute.Value = version;
            else reference.Element("Version")!.Value = version;
            context.Report.Change(StepName, $"{package} {current} → {version}", relative);
        }
    }

    /// <summary>True when <paramref name="current"/> is older than <paramref name="target"/> ("10.0.*" counts as the newest 10.0.x).</summary>
    internal static bool IsLower(string current, string target)
    {
        static Version? Parse(string v, int wildcard)
        {
            var parts = v.Split('-')[0].Split('.').Select(p => p == "*" ? wildcard : int.TryParse(p, out var n) ? n : -1).ToArray();
            return parts.Length >= 2 && parts.All(p => p >= 0)
                ? new Version(parts[0], parts[1], parts.Length > 2 ? parts[2] : 0)
                : null;
        }

        var c = Parse(current, 0);
        var t = Parse(target, int.MaxValue);
        return c is not null && t is not null && c < t;
    }

    private static void AddEmbeddedLocalization(XElement root, List<LocalizationResourceInfo> resources)
    {
        var properties = root.Elements("PropertyGroup").FirstOrDefault() ?? new XElement("PropertyGroup");
        if (properties.Parent is null) root.AddFirst(properties);
        if (properties.Element("GenerateEmbeddedFilesManifest") is null)
        {
            properties.Add(new XElement("GenerateEmbeddedFilesManifest", "true"));
        }

        var group = new XElement("ItemGroup");
        foreach (var resource in resources)
        {
            var path = $"Localization\\{resource.SourceName}\\*.json";
            group.Add(new XElement("EmbeddedResource", new XAttribute("Include", path)));
            group.Add(new XElement("Content", new XAttribute("Remove", path)));
        }

        root.Add(group);
    }

    internal static void Save(XDocument doc, string path)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            OmitXmlDeclaration = true,
            Encoding = new UTF8Encoding(false)
        };

        using (var writer = XmlWriter.Create(path, settings))
        {
            doc.Save(writer);
        }

        File.AppendAllText(path, Environment.NewLine);
    }

    /// <summary>Directory.Packages.props with ManagePackageVersionsCentrally.</summary>
    private sealed class CentralPackages
    {
        private readonly string _path;
        private readonly XDocument _doc;
        private bool _changed;

        private CentralPackages(string path, XDocument doc)
        {
            _path = path;
            _doc = doc;
        }

        public static CentralPackages? Find(string root)
        {
            var path = Path.Combine(root, "Directory.Packages.props");
            if (!File.Exists(path)) return null;
            var doc = XDocument.Load(path);
            var enabled = doc.Descendants("ManagePackageVersionsCentrally").Any(e => e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
            return enabled ? new CentralPackages(path, doc) : null;
        }

        public void Ensure(string package, string version)
        {
            if (FindVersion(package) is not null) return;
            var group = _doc.Root!.Elements("ItemGroup").FirstOrDefault(g => g.Elements("PackageVersion").Any());
            if (group is null)
            {
                group = new XElement("ItemGroup");
                _doc.Root.Add(group);
            }

            group.Add(new XElement("PackageVersion", new XAttribute("Include", package), new XAttribute("Version", version)));
            _changed = true;
        }

        public void Bump(string package, string version)
        {
            var element = FindVersion(package);
            var attribute = element?.Attribute("Version");
            if (attribute is not null && IsLower(attribute.Value, version))
            {
                attribute.Value = version;
                _changed = true;
            }
        }

        public void Save(MigrationContext context)
        {
            foreach (var element in _doc.Descendants("PackageVersion").ToList())
            {
                var package = element.Attribute("Include")?.Value ?? "";
                if (PackageMappings.IsBoilerplatePackage(package) || PackageMappings.IsObsoleteThirdParty(package))
                {
                    element.Remove();
                    _changed = true;
                }
            }

            if (_changed)
            {
                ProjectFileStep.Save(_doc, _path);
                context.Report.Change(StepName, "Updated Directory.Packages.props", context.Relative(_path));
            }
        }

        private XElement? FindVersion(string package) => _doc.Descendants("PackageVersion")
            .FirstOrDefault(e => string.Equals(e.Attribute("Include")?.Value, package, StringComparison.OrdinalIgnoreCase));
    }
}
