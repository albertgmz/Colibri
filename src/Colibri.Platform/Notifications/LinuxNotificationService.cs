using System.Collections.Concurrent;
using Colibri.Core.Models;
using Colibri.Core.Platform;
using Colibri.Platform.DBus;
using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;

namespace Colibri.Platform.Notifications;

/// <summary>
/// Linux: the freedesktop.org Desktop Notifications service (<c>org.freedesktop.Notifications</c> on the
/// session bus), implemented by GNOME, KDE, XFCE, dunst, mako and others. Without a session bus or a
/// notification server at the first notification, every call does nothing for the rest of the run (logged
/// once); nothing is thrown into the app.
/// </summary>
internal sealed class LinuxNotificationService : INotificationService
{
    private const string Service = "org.freedesktop.Notifications";
    private const string ObjectPath = "/org/freedesktop/Notifications";
    private const string Interface = "org.freedesktop.Notifications";

    // Desktop Notifications spec: the action with key "default" is invoked by clicking the notification
    // itself; servers do not show it as a button.
    internal const string DefaultActionKey = "default";

    private readonly SessionBus _bus;
    private readonly NotificationTexts _texts;
    private readonly ILogger<LinuxNotificationService> _logger;

    // Notification id (chosen by the server) -> download and what a click on the notification itself does,
    // for the notifications that are still open.
    private readonly ConcurrentDictionary<uint, (Guid DownloadId, NotificationAction DefaultAction)> _notifications = new();
    private readonly Lazy<Task<NotificationServer?>> _server;

    public LinuxNotificationService(SessionBus bus, NotificationTexts texts, ILogger<LinuxNotificationService> logger)
    {
        _bus = bus;
        _texts = texts;
        _logger = logger;
        _server = new Lazy<Task<NotificationServer?>>(ConnectAsync);
    }

    /// <summary>Raised on a D-Bus reader thread, not the UI thread.</summary>
    public event EventHandler<NotificationActionInvoked>? ActionInvoked;

    // Clicking a completed download's notification opens the file.
    public void ShowDownloadCompleted(DownloadItem item, string filePath) =>
        _ = NotifyAsync(item.Id, NotificationAction.Open, _texts.DownloadCompleteTitle, item.FileName, CompletedActions(_texts));

    // Clicking a failed download's notification shows Colibri's window; retrying needs the button.
    public void ShowDownloadFailed(DownloadItem item)
    {
        var body = string.IsNullOrWhiteSpace(item.ErrorMessage) ? item.FileName : $"{item.FileName}\n{item.ErrorMessage}";
        _ = NotifyAsync(item.Id, NotificationAction.Activate, _texts.DownloadFailedTitle, body, FailedActions(_texts));
    }

    /// <summary>The D-Bus actions (pairs of key and label) of a "completed" notification.</summary>
    internal static string[] CompletedActions(NotificationTexts texts) =>
    [
        DefaultActionKey, texts.Open,
        NotificationActionKeys.Open, texts.Open,
        NotificationActionKeys.ShowInFolder, texts.ShowInFolder,
    ];

    /// <summary>
    /// The D-Bus actions of a "failed" notification. Servers do not show "default" as a button; the few that
    /// list it in a menu show the app's name, which is what clicking brings up.
    /// </summary>
    internal static string[] FailedActions(NotificationTexts texts) =>
    [
        DefaultActionKey, "Colibri",
        NotificationActionKeys.Retry, texts.Retry,
    ];

    /// <summary>
    /// Maps an action key sent back by the server. "default" (a click on the notification itself) means
    /// <paramref name="defaultAction"/>, which depends on the kind of notification.
    /// </summary>
    internal static NotificationAction? ActionFromKey(string key, NotificationAction defaultAction) =>
        key == DefaultActionKey ? defaultAction : NotificationActionKeys.FromKey(key);

    /// <summary>
    /// Servers that announce "body-markup" read the body as a small subset of HTML, so a file name such as
    /// "Tom &amp; Jerry &lt;1&gt;.mp4" must be escaped there; the others show the body as plain text.
    /// </summary>
    internal static string FormatBody(string body, bool supportsMarkup) =>
        supportsMarkup ? body.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;") : body;

    private async Task NotifyAsync(Guid downloadId, NotificationAction defaultAction, string summary, string body, string[] actions)
    {
        try
        {
            if (await _server.Value.ConfigureAwait(false) is not { } server)
            {
                return;
            }

            var notificationId = await server.Connection.CallMethodAsync(
                CreateNotifyMessage(server.Connection, summary, FormatBody(body, server.SupportsMarkup), actions),
                (message, _) => message.GetBodyReader().ReadUInt32(),
                null).ConfigureAwait(false);
            _notifications[notificationId] = (downloadId, defaultAction);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not show a desktop notification");
        }
    }

    /// <summary>Subscribes to the server's signals and reads its capabilities, once.</summary>
    private async Task<NotificationServer?> ConnectAsync()
    {
        if (await _bus.GetConnectionAsync().ConfigureAwait(false) is not { } connection)
        {
            return null;
        }

        var subscriptions = new List<IDisposable>();
        try
        {
            // Subscribe before the first Notify so no click can be missed. Signals from every client's
            // notifications arrive here; only ids in _notifications are ours.
            subscriptions.Add(await connection.WatchSignalAsync(
                Service, ObjectPath, Interface, "ActionInvoked",
                (message, _) =>
                {
                    var reader = message.GetBodyReader();
                    return (Id: reader.ReadUInt32(), ActionKey: reader.ReadString());
                },
                (Notification<(uint Id, string ActionKey)> signal) =>
                {
                    if (signal.HasValue)
                    {
                        OnActionInvoked(signal.Value.Id, signal.Value.ActionKey);
                    }
                },
                ObserverFlags.None,
                emitOnCapturedContext: false).ConfigureAwait(false));

            subscriptions.Add(await connection.WatchSignalAsync(
                Service, ObjectPath, Interface, "NotificationClosed",
                (message, _) => message.GetBodyReader().ReadUInt32(),
                (Notification<uint> signal) =>
                {
                    if (signal.HasValue)
                    {
                        _notifications.TryRemove(signal.Value, out _);
                    }
                },
                ObserverFlags.None,
                emitOnCapturedContext: false).ConfigureAwait(false));

            // Also tells whether a notification server is running at all: without one this call fails.
            var capabilities = await connection.CallMethodAsync(
                CreateGetCapabilitiesMessage(connection),
                (message, _) => message.GetBodyReader().ReadArrayOfString(),
                null).ConfigureAwait(false);

            return new NotificationServer(connection, capabilities.Contains("body-markup"));
        }
        catch (Exception ex)
        {
            // Checked once per run: a notification server started later is not picked up.
            _logger.LogWarning(ex, "No desktop notification service is available; notifications are disabled");
            subscriptions.ForEach(subscription => subscription.Dispose());
            return null;
        }
    }

    // Notify(app_name, replaces_id, app_icon, summary, body, actions, hints, expire_timeout) -> id
    private static MessageBuffer CreateNotifyMessage(DBusConnection connection, string summary, string body, string[] actions)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Service, path: ObjectPath, @interface: Interface, member: "Notify", signature: "susssasa{sv}i");
        writer.WriteString("Colibri");
        writer.WriteUInt32(0);                                      // 0 = a new notification, not a replacement
        writer.WriteString(string.Empty);                           // no icon
        writer.WriteString(summary);
        writer.WriteString(body);
        writer.WriteArray(actions);                                 // pairs of key, label
        writer.WriteDictionaryEnd(writer.WriteDictionaryStart());   // no hints
        writer.WriteInt32(-1);                                      // the server's default timeout
        return writer.CreateMessage();
    }

    private static MessageBuffer CreateGetCapabilitiesMessage(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Service, path: ObjectPath, @interface: Interface, member: "GetCapabilities");
        return writer.CreateMessage();
    }

    private void OnActionInvoked(uint notificationId, string actionKey)
    {
        // The handler runs on the connection's reader: an exception here would close the connection.
        try
        {
            if (_notifications.TryGetValue(notificationId, out var notification)
                && ActionFromKey(actionKey, notification.DefaultAction) is { } action)
            {
                ActionInvoked?.Invoke(this, new NotificationActionInvoked(notification.DownloadId, action));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handling a notification action failed");
        }
    }

    private sealed record NotificationServer(DBusConnection Connection, bool SupportsMarkup);
}
