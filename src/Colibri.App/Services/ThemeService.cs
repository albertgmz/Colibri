using Avalonia;
using Avalonia.Styling;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Colibri.Core.Settings;

namespace Colibri.App.Services;

/// <summary>Applies the theme setting to the running app.</summary>
public static class ThemeService
{
    public static void ApplyAccent(string value)
    {
        if (!Color.TryParse(value, out var color)) color = Color.Parse("#C42B1C");
        if (Application.Current?.Styles.OfType<FluentTheme>().FirstOrDefault() is { } theme)
        {
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                if (!theme.Palettes.TryGetValue(variant, out var palette))
                    theme.Palettes[variant] = palette = new ColorPaletteResources();
                palette.Accent = color;
            }
        }
    }

    /// <summary>Changes the theme of every open window at once.</summary>
    public static void Apply(AppTheme theme)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                AppTheme.Light => ThemeVariant.Light,
                AppTheme.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default, // Follows the operating system.
            };
        }
    }
}
