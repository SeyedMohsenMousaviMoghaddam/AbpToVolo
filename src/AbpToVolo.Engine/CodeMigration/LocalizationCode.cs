using System.Globalization;
using AbpToVolo.Core;

namespace AbpToVolo.CodeMigration;

/// <summary>C# snippets for registering a converted localization resource in an ABP.io module.</summary>
public static class LocalizationCode
{
    /// <summary>Body of Configure&lt;AbpLocalizationOptions&gt;(options => { ... }).</summary>
    public static string OptionsBody(LocalizationResourceInfo loc) =>
        $"options.Resources.Add<{loc.TypeName}>(\"{loc.DefaultCulture}\").AddVirtualJson(\"{loc.VirtualPath}\"); " +
        $"options.DefaultResourceType = typeof({loc.TypeName});" +
        Languages(loc);

    /// <summary>Boilerplate stored languages in the database; ABP.io needs them in AbpLocalizationOptions.Languages.</summary>
    public static string Languages(LocalizationResourceInfo loc) => string.Concat((loc.Cultures ?? []).Select(culture =>
    {
        string name;
        try
        {
            name = CultureInfo.GetCultureInfo(culture).NativeName;
        }
        catch (CultureNotFoundException)
        {
            name = culture;
        }

        return $" options.Languages.Add(new LanguageInfo(\"{culture}\", \"{culture}\", \"{name.Replace("\"", "")}\"));";
    }));
}
