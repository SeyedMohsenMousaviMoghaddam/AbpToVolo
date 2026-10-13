using System.Text.RegularExpressions;
using System.Xml.Linq;
using AbpToVolo.Core;

namespace AbpToVolo.Steps;

/// <summary>
/// Microsoft.EntityFrameworkCore.* / Microsoft.Extensions.* references must be at least the version the chosen ABP
/// release depends on, otherwise NuGet fails with NU1605 (package downgrade). The version is read from the
/// Volo.Abp.EntityFrameworkCore nuspec on nuget.org; offline, a floating "major.minor.*" version is used.
/// </summary>
public sealed partial class ResolveVersionsStep : IMigrationStep
{
    private readonly Func<string, string?> _download;

    public ResolveVersionsStep(Func<string, string?>? download = null)
    {
        _download = download ?? Download;
    }

    public string Name => "Resolve package versions";

    public void Execute(MigrationContext context)
    {
        if (context.Options.MicrosoftPackagesVersion is { } explicitVersion)
        {
            context.MicrosoftPackagesVersion = explicitVersion;
            return;
        }

        var abp = context.Options.AbpVersion;
        var nuspec = _download($"https://api.nuget.org/v3-flatcontainer/volo.abp.entityframeworkcore/{abp}/volo.abp.entityframeworkcore.nuspec");
        var version = nuspec is null ? null : FindDependency(nuspec, "Microsoft.EntityFrameworkCore");

        if (version is not null)
        {
            context.MicrosoftPackagesVersion = version;
            context.Report.Info(Name, $"ABP {abp} depends on Microsoft packages {version}");
        }
        else
        {
            context.MicrosoftPackagesVersion = context.Options.DefaultMicrosoftPackagesVersion;
            context.Report.Warning(Name, $"Could not read ABP {abp} dependencies from nuget.org; using floating version " +
                                         $"{context.MicrosoftPackagesVersion} for Microsoft packages (pin it with --ms-version).");
        }

        context.Log.WriteLine($"    Microsoft packages: {context.MicrosoftPackagesVersion}");
    }

    internal static string? FindDependency(string nuspec, string packageId)
    {
        try
        {
            var dependency = XDocument.Parse(nuspec).Descendants()
                .Where(e => e.Name.LocalName == "dependency")
                .FirstOrDefault(e => string.Equals(e.Attribute("id")?.Value, packageId, StringComparison.OrdinalIgnoreCase));
            var range = dependency?.Attribute("version")?.Value;
            return range is null ? null : MinimumVersion().Match(range) is { Success: true } m ? m.Value : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? Download(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            return http.GetStringAsync(url).GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>"[10.0.11, )" or "10.0.11" → 10.0.11</summary>
    [GeneratedRegex(@"\d+\.\d+\.\d+(-[\w\.]+)?")]
    private static partial Regex MinimumVersion();
}
