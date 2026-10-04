using System.Globalization;
using System.Resources;
using Avalonia.Headless.XUnit;
using Colibri.App.Services;
using Colibri.Core.Settings;

namespace Colibri.App.Tests.Services;

public class LanguageServiceTests
{
    [Theory]
    [InlineData(null, "system")]
    [InlineData("", "system")]
    [InlineData("invalid-language-name", "system")]
    [InlineData(" EN ", "en")]
    [InlineData(" FR-ca ", "fr-CA")]
    public void Language_preferences_are_canonical(string? input, string expected) =>
        Assert.Equal(expected, LanguagePreference.Normalize(input));

    [Fact]
    public void Incomplete_bundled_translation_uses_parent_then_English_resources()
    {
        var resources = new ResourceManager("Colibri.App.Tests.Resources.TranslationFixture", typeof(LanguageServiceTests).Assembly);
        Assert.Equal("Bonjour", resources.GetString("Greeting", CultureInfo.GetCultureInfo("fr-CA")));
        Assert.Equal("English fallback", resources.GetString("MissingInFrench", CultureInfo.GetCultureInfo("fr-CA")));
        Assert.Equal("English fallback", resources.GetString("MissingInFrench", CultureInfo.GetCultureInfo("ja")));
        var bundled = LanguageService.DiscoverCultures(typeof(LanguageServiceTests).Assembly, resources);
        Assert.Contains(bundled, culture => culture.Name == "fr");
        Assert.Contains(bundled, culture => culture.Name == "en");
    }

    [AvaloniaFact]
    public void Startup_language_changes_UI_culture_without_changing_formatting()
    {
        var uiCulture = CultureInfo.CurrentUICulture;
        var defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
        var formatting = CultureInfo.CurrentCulture;
        try
        {
            LanguageService.ApplyStartup("en");
            Assert.Equal("en", CultureInfo.CurrentUICulture.Name);
            Assert.Same(formatting, CultureInfo.CurrentCulture);
            Assert.Equal("en", CultureInfo.DefaultThreadCurrentUICulture!.Name);
            Assert.Equal("en", LanguageService.ResolveCulture("future-language", CultureInfo.GetCultureInfo("fr-CA")).Name);
        }
        finally
        {
            CultureInfo.CurrentUICulture = uiCulture;
            CultureInfo.DefaultThreadCurrentUICulture = defaultUiCulture;
        }
    }
}
