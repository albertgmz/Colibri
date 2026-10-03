using Colibri.Core.Models;
using Colibri.Core.Platform;

namespace Colibri.App.Services;

// Used only when Colibri.Platform registers nothing for an interface (see AppServices): the app then
// still runs, without that feature.

/// <summary>Shows no notifications.</summary>
public sealed class NoOpNotificationService : INotificationService
{
    public event EventHandler<NotificationActionInvoked>? ActionInvoked
    {
        add { }
        remove { }
    }

    public void ShowDownloadCompleted(DownloadItem item, string filePath)
    {
    }

    public void ShowDownloadFailed(DownloadItem item)
    {
    }
}

/// <summary>Autostart is not supported.</summary>
public sealed class NoOpAutostartService : IAutostartService
{
    public bool IsSupported => false;

    public Task<bool> IsEnabledAsync() => Task.FromResult(false);

    public Task SetEnabledAsync(bool enabled) => Task.CompletedTask;
}

/// <summary>Shows no taskbar progress.</summary>
public sealed class NoOpTaskbarProgress : ITaskbarProgress
{
    public void SetProgress(nint windowHandle, double? fraction)
    {
    }
}

/// <summary>Assumes a tray is available (true on Windows and macOS).</summary>
public sealed class AssumeTrayAvailable : ITrayAvailability
{
    public Task<bool> IsAvailableAsync() => Task.FromResult(true);
}
