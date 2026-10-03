using Colibri.Core.Platform;

namespace Colibri.Platform.Notifications;

/// <summary>
/// The argument string of a Windows toast or toast button: the action and the download id, such as
/// <c>open;0f8fad5bd9cb469fa16570867728950e</c>. Windows hands the string back unchanged when the user
/// clicks, possibly to a freshly started Colibri process, so it must carry everything needed to act.
/// Kept free of Windows APIs so it can be tested on every OS.
/// </summary>
internal static class ToastArguments
{
    public static string Encode(NotificationAction action, Guid downloadId) =>
        $"{NotificationActionKeys.ToKey(action)};{downloadId:N}";

    /// <summary>Returns null for anything <see cref="Encode"/> did not produce.</summary>
    public static NotificationActionInvoked? Decode(string? arguments)
    {
        var parts = (arguments ?? string.Empty).Split(';');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[1], "N", out var downloadId))
        {
            return null;
        }

        return NotificationActionKeys.FromKey(parts[0]) is { } action
            ? new NotificationActionInvoked(downloadId, action)
            : null;
    }
}
