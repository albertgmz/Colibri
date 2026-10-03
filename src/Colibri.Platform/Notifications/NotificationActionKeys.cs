using Colibri.Core.Platform;

namespace Colibri.Platform.Notifications;

/// <summary>
/// Short text keys for notification actions, used in Windows toast arguments and as D-Bus action keys.
/// </summary>
internal static class NotificationActionKeys
{
    public const string Open = "open";
    public const string ShowInFolder = "folder";
    public const string Retry = "retry";

    public static string ToKey(NotificationAction action) => action switch
    {
        NotificationAction.Open => Open,
        NotificationAction.ShowInFolder => ShowInFolder,
        NotificationAction.Retry => Retry,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    /// <summary>Returns null for an unknown key.</summary>
    public static NotificationAction? FromKey(string? key) => key switch
    {
        Open => NotificationAction.Open,
        ShowInFolder => NotificationAction.ShowInFolder,
        Retry => NotificationAction.Retry,
        _ => null,
    };
}
