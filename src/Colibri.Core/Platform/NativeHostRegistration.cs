namespace Colibri.Core.Platform;

/// <summary>
/// Data written to the browser's native-messaging host manifest.
/// </summary>
public sealed record NativeHostRegistration
{
    /// <summary>Default host name used by the Colibri extension.</summary>
    public const string DefaultHostName = "com.colibri.host";

    public string HostName { get; init; } = DefaultHostName;

    public required string Description { get; init; }

    /// <summary>Full path of the native host executable.</summary>
    public required string HostExecutablePath { get; init; }

    /// <summary>Extension origins allowed to connect, e.g. <c>chrome-extension://&lt;id&gt;/</c>.</summary>
    public required IReadOnlyList<string> AllowedOrigins { get; init; }
}
