namespace Colibri.App.Services;

/// <summary>What closing the main window does.</summary>
public enum CloseAction
{
    HideToTray,
    Exit,
}

/// <summary>How the main window appears when Colibri starts.</summary>
public enum StartupWindowMode
{
    Shown,

    /// <summary>Not shown; only the tray icon is visible.</summary>
    HiddenInTray,

    /// <summary>Shown minimized to the taskbar (when <c>--minimized</c> is given but there is no tray).</summary>
    Minimized,
}

/// <summary>
/// The rules for hiding the main window in the tray. Without a tray icon a hidden window could not be
/// brought back, so then the window is never hidden.
/// </summary>
public static class WindowBehavior
{
    public const string MinimizedArgument = "--minimized";

    public static CloseAction OnClose(bool trayAvailable, bool closeToTray) =>
        trayAvailable && closeToTray ? CloseAction.HideToTray : CloseAction.Exit;

    public static bool HideOnMinimize(bool trayAvailable, bool minimizeToTray) => trayAvailable && minimizeToTray;

    public static StartupWindowMode OnStartup(bool minimizedArgument, bool trayAvailable) => minimizedArgument
        ? trayAvailable ? StartupWindowMode.HiddenInTray : StartupWindowMode.Minimized
        : StartupWindowMode.Shown;

    public static bool HasMinimizedArgument(IEnumerable<string>? args) =>
        args?.Any(a => string.Equals(a, MinimizedArgument, StringComparison.OrdinalIgnoreCase)) == true;
}
