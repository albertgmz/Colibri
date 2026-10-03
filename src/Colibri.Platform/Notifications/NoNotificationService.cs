using Colibri.Core.Models;
using Colibri.Core.Platform;

namespace Colibri.Platform.Notifications;

/// <summary>
/// Shows nothing. Used only when the plain net10.0 build of Colibri.Platform runs on Windows (for example
/// from a test project): toasts need the Windows target framework, which the app itself always uses.
/// </summary>
internal sealed class NoNotificationService : INotificationService
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
