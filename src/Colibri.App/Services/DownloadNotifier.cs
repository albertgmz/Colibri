using Avalonia.Threading;
using Colibri.Core.Models;
using Colibri.Core.Platform;
using Colibri.Core.Services;
using Microsoft.Extensions.Logging;

namespace Colibri.App.Services;

/// <summary>
/// Shows a notification when a download completes or fails during this session, and carries out the
/// action the user picks on a notification (open, show in folder, retry, show the window).
/// </summary>
/// <remarks>
/// <para>
/// Only changes it sees happen are announced: a download is announced when an update moves it from
/// another state to Completed or Failed. Downloads that were already finished when Colibri started are
/// never announced, because finished downloads get no updates.
/// </para>
/// <para>
/// Actions can arrive before the app is ready: a Windows toast clicked after Colibri exited starts a new
/// Colibri, and Windows delivers the click as soon as the notification service exists. Such actions wait
/// until <see cref="StartHandlingActions"/> is called, once the downloads are loaded and the window exists.
/// Actions may also refer to downloads of an earlier session, so the download is looked up when the action
/// is carried out.
/// </para>
/// </remarks>
public sealed class DownloadNotifier
{
    private readonly DownloadManager _manager;
    private readonly INotificationService _notifications;
    private readonly IShellService _shell;
    private readonly ILogger<DownloadNotifier> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DownloadState> _known = [];

    // Completed with the action that shows the main window, by StartHandlingActions.
    private readonly TaskCompletionSource<Action> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DownloadNotifier(DownloadManager manager, INotificationService notifications, IShellService shell, ILogger<DownloadNotifier> logger)
    {
        _manager = manager;
        _notifications = notifications;
        _shell = shell;
        _logger = logger;
    }

    /// <summary>
    /// Starts listening to downloads and notifications. Call before the download manager starts, and as early
    /// as possible on Windows (see the remarks).
    /// </summary>
    public void Start()
    {
        _manager.ItemAdded += (_, item) => Remember(item);
        _manager.ItemsUpdated += (_, items) => OnItemsUpdated(items);
        _manager.ItemRemoved += (_, id) =>
        {
            lock (_gate)
            {
                _known.Remove(id);
            }
        };
        _notifications.ActionInvoked += (_, e) => _ = OnActionAsync(e);
    }

    /// <summary>
    /// Carries out the notification actions that arrived so far and those that come later. Call once the
    /// download manager is initialized and the main window can be shown.
    /// </summary>
    /// <param name="showMainWindow">Shows and restores the main window; called on the UI thread.</param>
    public void StartHandlingActions(Action showMainWindow) => _ready.TrySetResult(showMainWindow);

    private void Remember(DownloadItem item)
    {
        lock (_gate)
        {
            _known[item.Id] = item.State;
        }
    }

    private void OnItemsUpdated(IReadOnlyList<DownloadItem> items)
    {
        foreach (var item in items)
        {
            DownloadState? previous;
            lock (_gate)
            {
                previous = _known.TryGetValue(item.Id, out var known) ? known : null;
                _known[item.Id] = item.State;
            }

            // The manager raises its events on background threads; notification code may expect the UI thread.
            if (previous is { } before && before != item.State)
            {
                if (item.State == DownloadState.Completed)
                {
                    Dispatcher.UIThread.Post(() => _notifications.ShowDownloadCompleted(item, FilePath(item)));
                }
                else if (item.State == DownloadState.Failed)
                {
                    Dispatcher.UIThread.Post(() => _notifications.ShowDownloadFailed(item));
                }
            }
        }
    }

    private async Task OnActionAsync(NotificationActionInvoked e)
    {
        var showMainWindow = await _ready.Task;
        try
        {
            switch (e.Action)
            {
                case NotificationAction.Activate:
                    Dispatcher.UIThread.Post(showMainWindow);
                    break;
                case NotificationAction.Open:
                    if (await FindAsync(e.DownloadId) is { } toOpen)
                    {
                        await _shell.OpenFileAsync(FilePath(toOpen));
                    }

                    break;
                case NotificationAction.ShowInFolder:
                    if (await FindAsync(e.DownloadId) is { } toReveal)
                    {
                        await _shell.RevealInFolderAsync(FilePath(toReveal));
                    }

                    break;
                case NotificationAction.Retry:
                    await _manager.RetryAsync(e.DownloadId, CancellationToken.None);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not carry out the notification action {Action}", e.Action);
        }
    }

    /// <summary>The download, or null when it has been deleted since the notification was shown.</summary>
    private async Task<DownloadItem?> FindAsync(Guid id) =>
        (await _manager.GetItemsAsync(CancellationToken.None)).FirstOrDefault(item => item.Id == id);

    private static string FilePath(DownloadItem item) => Path.Combine(item.SaveFolder, item.FileName);
}
