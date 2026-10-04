using System.Text;
using Colibri.Platform.DBus;
using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;

namespace Colibri.Platform.Shell;

/// <summary>
/// Linux: <c>xdg-open</c> opens files and folders with the desktop's default programs. "Show in folder"
/// asks the file manager over D-Bus (<c>org.freedesktop.FileManager1.ShowItems</c>, implemented by
/// Nautilus, Dolphin, Nemo, Caja, Thunar and others) to select the file; without one it falls back to
/// opening the folder.
/// </summary>
internal sealed class LinuxShellService(SessionBus bus, ILogger<LinuxShellService> logger) : ShellServiceBase(logger)
{
    private const string FileManagerService = "org.freedesktop.FileManager1";
    private const string FileManagerPath = "/org/freedesktop/FileManager1";
    private static readonly TimeSpan ShowItemsTimeout = TimeSpan.FromSeconds(5);

    protected override Task OpenAsync(string path)
    {
        ProcessLauncher.TryStart("xdg-open", [path], Logger);
        return Task.CompletedTask;
    }

    protected override async Task SelectAsync(string path)
    {
        if (await bus.GetConnectionAsync().ConfigureAwait(false) is { } connection)
        {
            try
            {
                // D-Bus activation starts the file manager if it is installed but not running. D-Bus calls
                // have no timeout of their own, so a file manager that hangs is given up on after a while.
                await connection.CallMethodAsync(CreateShowItemsMessage(connection, FileUri(path)))
                    .WaitAsync(ShowItemsTimeout).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                Logger.LogInformation(ex, "No file manager answered ShowItems; opening the folder instead");
            }
        }

        if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
        {
            await OpenAsync(folder).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A <c>file://</c> URI for an absolute path, every segment percent-encoded, so names containing
    /// spaces, '#', '%' or '?' survive.
    /// </summary>
    internal static string FileUri(string absolutePath)
    {
        var builder = new StringBuilder("file://");
        foreach (var segment in absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            builder.Append('/').Append(Uri.EscapeDataString(segment));
        }

        return builder.ToString();
    }

    // ShowItems(array of URIs, startup id for focus stealing prevention; empty = none)
    private static MessageBuffer CreateShowItemsMessage(DBusConnection connection, string uri)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: FileManagerService, path: FileManagerPath, @interface: FileManagerService, member: "ShowItems", signature: "ass");
        writer.WriteArray(new[] { uri });
        writer.WriteString(string.Empty);
        return writer.CreateMessage();
    }
}
