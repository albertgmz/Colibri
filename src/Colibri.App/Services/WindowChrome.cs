using Avalonia.Controls;

namespace Colibri.App.Services;

/// <summary>
/// How the main window's frame looks on this OS. The implementation is chosen once in DI, so views
/// never check the operating system.
/// </summary>
/// <remarks>
/// It works through two style classes on the window, used by the styles in MainWindow.axaml:
/// "extended" (content drawn under the title bar, so the app shows its own title row) and
/// "mica" (the Mica backdrop is active, so panes use translucent backgrounds).
/// </remarks>
public interface IWindowChrome
{
    void Apply(Window window);
}

/// <summary>Linux and macOS: the normal system title bar and solid backgrounds.</summary>
public sealed class DefaultWindowChrome : IWindowChrome
{
    public void Apply(Window window)
    {
    }
}

/// <summary>
/// Windows: content extended into the title bar (the system caption buttons stay) and the Mica
/// backdrop on Windows 11. Older Windows has no Mica; Avalonia then falls back to the next hint (a
/// normal opaque window) and the panes keep their solid theme colors.
/// </summary>
public sealed class WindowsWindowChrome : IWindowChrome
{
    public void Apply(Window window)
    {
        window.ExtendClientAreaToDecorationsHint = true;
        window.Classes.Add("extended");
        window.TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.None];

        // Which level Windows actually granted is only known once the window exists, and can change
        // (for example when transparency effects are turned off in the Windows settings).
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == TopLevel.ActualTransparencyLevelProperty)
            {
                UpdateBackground(window);
            }
        };
        window.Opened += (_, _) => UpdateBackground(window);
    }

    // The "mica" class makes the window background transparent (the backdrop is only visible through
    // it) and switches the panes to translucent brushes; see MainWindow.axaml.
    private static void UpdateBackground(Window window) =>
        window.Classes.Set("mica", window.ActualTransparencyLevel == WindowTransparencyLevel.Mica);
}
