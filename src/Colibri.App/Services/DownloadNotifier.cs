using Avalonia.Threading;
using Colibri.Core.Models;
using Colibri.Core.Platform;
using Colibri.Core.Services;
using Microsoft.Extensions.Logging;

namespace Colibri.App.Services;

/// <summary>
/// Shows a notification when a download completes or fails during this session, and carries out the
/// action the user picks on it (open, show in folder, retry).
/// </summary>
/// <remarks>
/// Only changes it sees happen are announced: a download is announced when an update moves it from
/// another state to Completed or Failed. Downloads that were already finished when Colibri started are
/// never announced, because finished downloads get no updates.
/// </remarks>
public sealed class DownloadNotifier
{
    private readonly DownloadManager _manager;
    private readonly INotificationService _notifications;
    private readonly IShellService _shell;
    private readonly ILogger<DownloadNotifier> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (DownloadState State, string FilePath)> _known = [];

    public DownloadNotifier(DownloadManager manager, INotificationService notifications, IShellService shell, ILogger<DownloadNotifier> logger)
    {
        _manager = manager;
        _notifications = notifications;
        _shell = shell;
        _logger = logger;
    }

    /// <summary>Starts listening; call before the download manager starts.</summary>
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

    private void Remember(DownloadItem item)
    {
        lock (_gate)
        {
            _known[item.Id] = (item.State, FilePath(item));
        }
    }

    private void OnItemsUpdated(IReadOnlyList<DownloadItem> items)
    {
        foreach (var item in items)
        {
            DownloadState? previous;
            lock (_gate)
            {
                previous = _known.TryGetValue(item.Id, out var known) ? known.State : null;
                _known[item.Id] = (item.State, FilePath(item));
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
        string? path;
        lock (_gate)
        {
            path = _known.TryGetValue(e.DownloadId, out var known) ? known.FilePath : null;
        }

        try
        {
            switch (e.Action)
            {
                case NotificationAction.Open when path is not null:
                    await _shell.OpenFileAsync(path);
                    break;
                case NotificationAction.ShowInFolder when path is not null:
                    await _shell.RevealInFolderAsync(path);
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

    private static string FilePath(DownloadItem item) => Path.Combine(item.SaveFolder, item.FileName);
}
