namespace Colibri.Core.Platform;

/// <summary>
/// Tells whether the desktop can show a tray (notification area) icon. Some Linux desktops cannot,
/// and then closing the main window must exit instead of hiding it.
/// </summary>
public interface ITrayAvailability
{
    /// <summary>Returns true when a tray icon can be shown.</summary>
    Task<bool> IsAvailableAsync();
}
