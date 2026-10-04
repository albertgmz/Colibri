using Colibri.Core.Models;

namespace Colibri.Core.Platform;

/// <summary>
/// Shows operating system notifications.
/// </summary>
public interface INotificationService
{
    /// <summary>Raised when the user clicks a notification or one of its buttons.</summary>
    event EventHandler<NotificationActionInvoked>? ActionInvoked;

    /// <summary>Shows a "download completed" notification with Open and Show in folder actions.</summary>
    void ShowDownloadCompleted(DownloadItem item, string filePath);

    /// <summary>Shows a "download failed" notification with a Retry action.</summary>
    void ShowDownloadFailed(DownloadItem item);
}
