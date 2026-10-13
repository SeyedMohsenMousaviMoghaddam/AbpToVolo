using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AbpToVolo.Core;

namespace AbpToVolo.Steps;

/// <summary>
/// Converts Boilerplate XML localization dictionaries (&lt;localizationDictionary culture="en"&gt;) into ABP.io JSON
/// resources (Localization/{Source}/{culture}.json) and generates the typed resource class.
/// </summary>
public sealed partial class LocalizationStep : IMigrationStep
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping // keep Persian/Arabic text readable
    };

    public string Name => "Localization (XML → JSON)";

    public void Execute(MigrationContext context)
    {
        var dictionaries = FileSystem.EnumerateFiles(context.OutputRoot, "*.xml")
            .Select(file => (File: file, Doc: TryLoad(file)))
            .Where(x => x.Doc?.Root?.Name.LocalName == "localizationDictionary")
            .ToList();

        if (dictionaries.Count == 0)
        {
            context.Report.Info(Name, "No XML localization dictionaries found.");
            return;
        }

        var sources = dictionaries
            .Select(d => (d.File, d.Doc, Culture: d.Doc!.Root!.Attribute("culture")?.Value ?? "en"))
            .GroupBy(d => (Directory: Path.GetDirectoryName(d.File)!, Source: SourceName(d.File, d.Culture)));

        foreach (var source in sources)
        {
            var project = context.FindProjectForFile(source.Key.Directory);
            if (project is null)
            {
                context.Report.Warning(Name, $"Localization source '{source.Key.Source}' is not inside a project; skipped.");
                continue;
            }

            // With contracts enabled the resources belong to *.Domain.Shared (visible to every layer, as in the ABP.io template).
            var owner = context.FindProject(ProjectLayer.DomainShared) ?? project;
            var resourceFolder = Path.Combine(owner.Directory, "Localization", source.Key.Source);
            foreach (var (file, doc, culture) in source)
            {
                var texts = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var text in doc!.Root!.Descendants().Where(e => e.Name.LocalName == "text"))
                {
                    var name = text.Attribute("name")?.Value;
                    if (name is null) continue;
                    texts[name] = text.Attribute("value")?.Value ?? text.Value.Trim();
                }

                var json = JsonSerializer.Serialize(new { culture, texts }, JsonOptions);
                var target = Path.Combine(resourceFolder, culture + ".json");
                FileSystem.WriteText(target, json + Environment.NewLine);
                context.Report.Change(Name, $"{context.Relative(file)} → {context.Relative(target)} ({texts.Count} texts)");
            }

            var defaultCulture = source.FirstOrDefault(s => Path.GetFileNameWithoutExtension(s.File) == source.Key.Source).Culture
                                 ?? source.First().Culture;
            var typeName = ToIdentifier(source.Key.Source) + "Resource";
            var ns = owner.RootNamespace + ".Localization";
            WriteResourceClass(owner, source.Key.Source, typeName, ns);

            context.LocalizationResources.Add(new LocalizationResourceInfo(
                source.Key.Source, typeName, ns, $"/Localization/{source.Key.Source}", defaultCulture, owner.Name,
                source.Select(s => s.Culture).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c != defaultCulture).ThenBy(c => c).ToList()));
            context.Report.Change(Name, $"Generated localization resource class {ns}.{typeName}");

            DeleteXmlConfigurer(context, project);
        }

        if (context.LocalizationResources.Count > 1)
        {
            context.Report.Todo(Name, $"{context.LocalizationResources.Count} localization sources found; code is mapped to " +
                                      $"'{context.PrimaryLocalization!.TypeName}'. Register the other resources in AbpLocalizationOptions.");
        }
    }

    private static XDocument? TryLoad(string file)
    {
        try
        {
            return XDocument.Load(file);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>PhoneBook-fa.xml (culture fa) → PhoneBook; PhoneBook.xml → PhoneBook.</summary>
    internal static string SourceName(string file, string culture)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var suffix = "-" + culture;
        return name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? name[..^suffix.Length] : name;
    }

    private static string ToIdentifier(string name)
    {
        var cleaned = NonIdentifier().Replace(name, "");
        return cleaned.Length > 0 && char.IsDigit(cleaned[0]) ? "_" + cleaned : cleaned;
    }

    private static void WriteResourceClass(ProjectInfo project, string sourceName, string typeName, string ns)
    {
        var path = Path.Combine(project.Directory, "Localization", typeName + ".cs");
        var code = $$"""
            using Volo.Abp.Localization;

            namespace {{ns}};

            [LocalizationResourceName("{{sourceName}}")]
            public class {{typeName}}
            {
            }

            """;
        FileSystem.WriteText(path, code);
    }

    /// <summary>The Boilerplate *LocalizationConfigurer registers XML sources and cannot compile against ABP.io.</summary>
    private static void DeleteXmlConfigurer(MigrationContext context, ProjectInfo project)
    {
        foreach (var file in FileSystem.EnumerateFiles(project.Directory, "*LocalizationConfigurer.cs"))
        {
            var text = File.ReadAllText(file);
            if (text.Contains("XmlEmbeddedFileLocalizationDictionaryProvider", StringComparison.Ordinal) ||
                text.Contains("DictionaryBasedLocalizationSource", StringComparison.Ordinal))
            {
                File.Delete(file);
                context.Report.Change("Localization (XML → JSON)", $"Deleted {context.Relative(file)} (replaced by AbpLocalizationOptions in the module)");
            }
        }
    }

    [GeneratedRegex(@"[^\w]")]
    private static partial Regex NonIdentifier();
}
