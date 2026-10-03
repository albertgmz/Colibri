namespace Colibri.Core.Platform;

/// <summary>
/// Registration state of the browser native-messaging host.
/// </summary>
public enum BrowserIntegrationStatus
{
    NotRegistered,
    Registered,

    /// <summary>Registered, but pointing to a different manifest, host path or origin list, the manifest is unreadable, or the host file is gone.</summary>
    Outdated,
}
