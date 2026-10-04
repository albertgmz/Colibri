using System.Globalization;
using System.Reflection;
using System.Resources;
using Colibri.App.Resources;
using Colibri.Core.Settings;

namespace Colibri.App.Services;

/// <summary>Uses ordinary bundled .NET satellites; settings changes take effect on the next start.</summary>
public static class LanguageService
{
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;
    private static readonly Lazy<IReadOnlyList<CultureInfo>> BundledCultures = new(() => DiscoverCultures(typeof(Strings).Assembly, Strings.ResourceManager));

    public static IReadOnlyList<CultureInfo> AvailableCultures => BundledCultures.Value;

    internal static IReadOnlyList<CultureInfo> DiscoverCultures(Assembly assembly, ResourceManager resources)
    {
        var directory = Path.GetDirectoryName(assembly.Location) ?? AppContext.BaseDirectory;
        var satelliteName = assembly.GetName().Name + ".resources.dll";
        var cultures = new List<CultureInfo> { CultureInfo.GetCultureInfo("en") };
        foreach (var folder in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(folder);
            if (LanguagePreference.Normalize(name) == "system" || name.Equals("en", StringComparison.OrdinalIgnoreCase)) continue;
            var culture = CultureInfo.GetCultureInfo(name);
            if (File.Exists(Path.Combine(folder, satelliteName)) && resources.GetResourceSet(culture, true, false) is not null)
                cultures.Add(culture);
        }
        return cultures.OrderBy(culture => culture.NativeName, StringComparer.Ordinal).ToArray();
    }

    public static CultureInfo ResolveCulture(string preference, CultureInfo systemCulture)
    {
        var id = LanguagePreference.Normalize(preference);
        var requested = id == "system" ? systemCulture : CultureInfo.GetCultureInfo(id);
        for (var candidate = requested; candidate.Name.Length > 0; candidate = candidate.Parent)
        {
            if (AvailableCultures.Any(culture => culture.Name.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase))) return requested;
        }
        return CultureInfo.GetCultureInfo("en");
    }

    public static void ApplyStartup(string preference)
    {
        var culture = ResolveCulture(preference, SystemCulture);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        // Leave CurrentCulture and DefaultThreadCurrentCulture unchanged: formatting is independent.
        Strings.Culture = null;
    }
}
