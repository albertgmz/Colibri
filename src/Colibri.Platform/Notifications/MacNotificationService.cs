using System.Text;
using Colibri.Core.Models;
using Colibri.Core.Platform;
using Microsoft.Extensions.Logging;

namespace Colibri.Platform.Notifications;

/// <summary>
/// macOS: notifications through AppleScript's <c>display notification</c>, run by <c>osascript</c>.
/// </summary>
/// <remarks>
/// The native API (UNUserNotificationCenter) only works from a signed app bundle, which a plain .NET
/// process is not. AppleScript notifications have no buttons and report no clicks, so
/// <see cref="ActionInvoked"/> is never raised. They are listed under "Script Editor" in System Settings
/// &gt; Notifications, and macOS may ask the user to allow them there first.
/// </remarks>
internal sealed class MacNotificationService(NotificationTexts texts, ILogger<MacNotificationService> logger) : INotificationService
{
    // Never raised (see remarks); explicit accessors avoid the "event is never used" warning.
    public event EventHandler<NotificationActionInvoked>? ActionInvoked
    {
        add { }
        remove { }
    }

    public void ShowDownloadCompleted(DownloadItem item, string filePath) =>
        Show(texts.DownloadCompleteTitle, item.FileName);

    public void ShowDownloadFailed(DownloadItem item) =>
        Show(texts.DownloadFailedTitle, string.IsNullOrWhiteSpace(item.ErrorMessage) ? item.FileName : $"{item.FileName}\n{item.ErrorMessage}");

    private void Show(string title, string body) =>
        ProcessLauncher.TryStart("osascript", ["-e", BuildScript(title, body)], logger);

    internal static string BuildScript(string title, string body) =>
        $"display notification {AppleScriptString(body)} with title {AppleScriptString(title)}";

    /// <summary>
    /// An AppleScript string literal. Inside double quotes AppleScript treats backslash as an escape
    /// character, so backslashes and quotes are escaped, and line breaks and tabs use their escapes.
    /// </summary>
    internal static string AppleScriptString(string value)
    {
        var builder = new StringBuilder("\"", value.Length + 2);
        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '\\' => @"\\",
                '"' => "\\\"",
                '\n' => @"\n",
                '\r' => @"\r",
                '\t' => @"\t",
                _ => c.ToString(),
            });
        }

        return builder.Append('"').ToString();
    }
}
