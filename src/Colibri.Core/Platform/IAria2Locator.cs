namespace Colibri.Core.Platform;

/// <summary>
/// Finds the aria2c executable.
/// </summary>
public interface IAria2Locator
{
    /// <summary>
    /// Returns the full path of aria2c: <paramref name="configuredPath"/> if it exists,
    /// otherwise the bundled or system copy; null if none is found.
    /// </summary>
    string? FindAria2(string? configuredPath);
}
