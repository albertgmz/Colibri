using Colibri.Core.Platform;
using Colibri.Platform.DBus;
using Microsoft.Extensions.Logging;

namespace Colibri.Platform.Tray;

/// <summary>
/// Linux: Avalonia's tray icon uses the StatusNotifierItem D-Bus protocol, which works only when a
/// "StatusNotifierWatcher" runs on the session bus (KDE Plasma, XFCE, Cinnamon, most others; on GNOME only
/// with the AppIndicator extension). Without one the icon would silently not appear.
/// </summary>
internal sealed class LinuxTrayAvailability(SessionBus bus, ILogger<LinuxTrayAvailability> logger) : ITrayAvailability
{
    private const string WatcherName = "org.kde.StatusNotifierWatcher";

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            return await bus.GetConnectionAsync().ConfigureAwait(false) is { } connection
                && await SessionBus.NameHasOwnerAsync(connection, WatcherName).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Could not check for a tray host; assuming there is none");
            return false;
        }
    }
}
