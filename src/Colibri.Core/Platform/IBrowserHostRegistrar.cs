namespace Colibri.Core.Platform;

/// <summary>
/// Registers the native-messaging host with the user's Chromium-based browsers (Chrome and Edge, and
/// Chromium on Linux and macOS), for the current user only.
/// </summary>
public interface IBrowserHostRegistrar
{
    /// <summary>
    /// How each supported browser is set up compared with <paramref name="expected"/>. Windows always lists
    /// Chrome and Edge; Linux and macOS list the browsers whose profile folder exists.
    /// </summary>
    Task<IReadOnlyList<BrowserHostStatus>> GetStatusAsync(NativeHostRegistration expected);

    /// <summary>Writes (or overwrites) the host manifest and registers it with every supported browser.</summary>
    Task RegisterAsync(NativeHostRegistration registration);
}

/// <summary>A browser that can use Colibri's native-messaging host.</summary>
public enum BrowserKind
{
    Chrome,
    Edge,
    Chromium,
    Firefox,
}

/// <summary>The registration state for one browser.</summary>
public sealed record BrowserHostStatus(BrowserKind Browser, BrowserIntegrationStatus Status);
