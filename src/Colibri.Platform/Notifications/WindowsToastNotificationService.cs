#if WINDOWS
using System.Runtime.Versioning;
using Colibri.Core.Models;
using Colibri.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Colibri.Platform.Notifications;

/// <summary>
/// Windows toast notifications through the Windows Community Toolkit's <c>ToastNotificationManagerCompat</c>,
/// which makes toasts work for an unpackaged (non-MSIX) app.
/// </summary>
/// <remarks>
/// <para>
/// The first use of <c>ToastNotificationManagerCompat</c> (the subscription in the constructor) registers
/// Colibri with Windows, per user, and keeps it registered: an app id derived from the exe path under
/// <c>HKCU\Software\Classes\AppUserModelId\&lt;id&gt;</c> (display name, icon, COM activator), a COM
/// server entry <c>HKCU\Software\Classes\CLSID\{guid}\LocalServer32</c> = <c>"&lt;exe&gt;" -ToastActivated</c>,
/// and the app icon under <c>%LOCALAPPDATA%\ToastNotificationManagerCompat\Apps\&lt;id&gt;</c>. When run
/// elevated it also writes the CLSID and an AppID under HKLM.
/// </para>
/// <para>
/// <c>ToastNotificationManagerCompat.Uninstall()</c> is deliberately not called on exit: it deletes that
/// registration and clears the toasts in Action Center, so their buttons would stop working as soon as
/// Colibri closes. Removing it belongs to an uninstaller.
/// </para>
/// <para>
/// Clicking a toast while Colibri is running calls back into the running process. Clicking one after
/// Colibri exited makes Windows start the exe with the arguments <c>-ToastActivated -Embedding</c> (that
/// new process is the primary instance) and deliver the click through COM once this service has been
/// created. The app must therefore create this service early at startup and ignore those two arguments;
/// <see cref="ActionInvoked"/> then fires in the new process.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.17763.0")]
internal sealed class WindowsToastNotificationService : INotificationService, IDisposable
{
    private readonly NotificationTexts _texts;
    private readonly ILogger<WindowsToastNotificationService> _logger;

    public WindowsToastNotificationService(NotificationTexts texts, ILogger<WindowsToastNotificationService> logger)
    {
        _texts = texts;
        _logger = logger;
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;
    }

    /// <summary>Raised on a COM thread, not the UI thread.</summary>
    public event EventHandler<NotificationActionInvoked>? ActionInvoked;

    public void ShowDownloadCompleted(DownloadItem item, string filePath)
    {
        // Clicking the toast body opens the file, like the Open button.
        var builder = new ToastContentBuilder()
            .AddToastActivationInfo(ToastArguments.Encode(NotificationAction.Open, item.Id), ToastActivationType.Foreground)
            .AddText(_texts.DownloadCompleteTitle)
            .AddText(item.FileName)
            .AddButton(new ToastButton(_texts.Open, ToastArguments.Encode(NotificationAction.Open, item.Id)))
            .AddButton(new ToastButton(_texts.ShowInFolder, ToastArguments.Encode(NotificationAction.ShowInFolder, item.Id)));
        Show(item.Id, builder);
    }

    public void ShowDownloadFailed(DownloadItem item)
    {
        // No activation arguments on the body: clicking it only dismisses the toast (or starts Colibri if it
        // is not running). Retrying needs the explicit button.
        var builder = new ToastContentBuilder()
            .AddText(_texts.DownloadFailedTitle)
            .AddText(item.FileName);
        if (!string.IsNullOrWhiteSpace(item.ErrorMessage))
        {
            builder.AddText(item.ErrorMessage);
        }

        builder.AddButton(new ToastButton(_texts.Retry, ToastArguments.Encode(NotificationAction.Retry, item.Id)));
        Show(item.Id, builder);
    }

    public void Dispose() => ToastNotificationManagerCompat.OnActivated -= OnToastActivated;

    private void Show(Guid downloadId, ToastContentBuilder builder)
    {
        try
        {
            // The tag makes a newer toast for the same download replace the older one in Action Center.
            builder.Show(toast => toast.Tag = downloadId.ToString("N"));
        }
        catch (Exception ex)
        {
            // Notifications can be blocked by policy or broken registration; a download must not fail for it.
            _logger.LogWarning(ex, "Could not show a toast notification");
        }
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        if (ToastArguments.Decode(e.Argument) is not { } invoked)
        {
            return;
        }

        try
        {
            ActionInvoked?.Invoke(this, invoked);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handling a toast action failed");
        }
    }
}
#endif
