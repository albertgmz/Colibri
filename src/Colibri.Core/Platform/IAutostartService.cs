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

    /// <summary>Enables or disables autostart of <paramref name="executablePath"/>.</summary>
    Task SetEnabledAsync(bool enabled, string executablePath);
}
