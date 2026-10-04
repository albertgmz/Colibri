using Avalonia;
using Avalonia.Styling;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Controls;
using Avalonia.Threading;
using Colibri.App.Resources;
using Colibri.Core.Settings;

namespace Colibri.App.Services;

/// <summary>Applies the theme setting to the running app.</summary>
public static class ThemeService
{
    private static string _palette = "warm";
    private static Color _accent = Color.Parse("#C42B1C");
    private static ResourceDictionary? _paletteResources;

    public static void ApplyAppearance(AppTheme theme, string palette, string accent)
    {
        Apply(theme);
        ApplyAccent(accent);
        ApplyPalette(palette);
    }

    public static void ApplyPalette(string value)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (Application.Current is not { } app) return;
        var id = BackgroundPalettes.Normalize(value);
        // The dictionary identity also avoids stale static state when a test creates a new application.
        if (_palette == id && _paletteResources is not null && app.Resources.MergedDictionaries.Contains(_paletteResources)) return;
        var previous = app.Styles.OfType<FluentTheme>().Single();
        var replacement = new FluentTheme { DensityStyle = previous.DensityStyle };
        var colors = DesignPalettes.Get(id);
        foreach (var (variant, surfaces) in new[] { (ThemeVariant.Light, colors.Light), (ThemeVariant.Dark, colors.Dark) })
        {
            var palette = new ColorPaletteResources { Accent = _accent };
            if (id != "warm")
            {
                palette.RegionColor = Color.Parse(surfaces.Pane);
                palette.ChromeLow = Color.Parse(surfaces.Chrome);
                palette.ChromeMedium = Color.Parse(surfaces.Pane);
                palette.ChromeMediumLow = Color.Parse(surfaces.Card);
            }
            replacement.Palettes[variant] = palette;
        }
        var resources = DesignPalettes.Create(id);
        var resourceIndex = _paletteResources is null ? -1 : app.Resources.MergedDictionaries.IndexOf(_paletteResources);
        if (resourceIndex < 0) app.Resources.MergedDictionaries.Add(resources);
        else app.Resources.MergedDictionaries[resourceIndex] = resources;
        app.Styles[app.Styles.IndexOf(previous)] = replacement;
        _paletteResources = resources;
        _palette = id;
    }

    public static void ApplyAccent(string value)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!Color.TryParse(value, out var color)) color = Color.Parse("#C42B1C");
        _accent = color;
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
