using Avalonia;
using Avalonia.Styling;
using Colibri.Core.Settings;

namespace Colibri.App.Services;

/// <summary>Applies the theme setting to the running app.</summary>
public static class ThemeService
{
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
