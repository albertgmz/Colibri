namespace Colibri.Core.Platform;

/// <summary>
/// Starts Colibri when the user logs in.
/// </summary>
public interface IAutostartService
{
    /// <summary>Whether autostart can be configured on this system.</summary>
    bool IsSupported { get; }

    /// <summary>Whether autostart is currently enabled.</summary>
    Task<bool> IsEnabledAsync();

    /// <summary>
    /// Enables or disables autostart. The implementation works out what to launch (the running
    /// executable, or the .app bundle / AppImage that contains it) and passes <c>--minimized</c>.
    /// </summary>
    Task SetEnabledAsync(bool enabled);
}
