namespace Colibri.Core.Platform;

/// <summary>
/// Registration state of the browser native-messaging host.
/// </summary>
public enum BrowserIntegrationStatus
{
    NotRegistered,
    Registered,

    /// <summary>Registered, but pointing to a different host path or origin list.</summary>
    Outdated,
}
