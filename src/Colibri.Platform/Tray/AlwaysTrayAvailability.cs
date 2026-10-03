using Colibri.Core.Platform;

namespace Colibri.Platform.Tray;

/// <summary>Windows and macOS always have a notification area / menu bar for the tray icon.</summary>
internal sealed class AlwaysTrayAvailability : ITrayAvailability
{
    public Task<bool> IsAvailableAsync() => Task.FromResult(true);
}
