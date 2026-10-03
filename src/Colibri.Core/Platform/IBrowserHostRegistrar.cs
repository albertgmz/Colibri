namespace Colibri.Core.Platform;

/// <summary>
/// Registers the native-messaging host with Chrome and Edge.
/// </summary>
public interface IBrowserHostRegistrar
{
    /// <summary>Returns the current registration state.</summary>
    Task<BrowserIntegrationStatus> GetStatusAsync();

    /// <summary>Writes (or overwrites) the host manifest and its registration.</summary>
    Task RegisterAsync(NativeHostRegistration info);
}
