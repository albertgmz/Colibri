namespace Colibri.Core.Platform;

/// <summary>
/// Button or click the user chose on a notification.
/// </summary>
public enum NotificationAction
{
    Open,
    ShowInFolder,
    Retry,

    /// <summary>Show and restore Colibri's main window.</summary>
    Activate,
}
