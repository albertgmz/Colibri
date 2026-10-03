using Colibri.Core.Platform;

namespace Colibri.Platform.Aria2;

/// <summary>Windows: the configured aria2c.exe, otherwise the copy shipped next to Colibri.</summary>
internal sealed class WindowsAria2Locator : IAria2Locator
{
    private readonly string _bundledPath;

    public WindowsAria2Locator()
        : this(Path.Combine(AppContext.BaseDirectory, "aria2", "aria2c.exe"))
    {
    }

    internal WindowsAria2Locator(string bundledPath)
    {
        _bundledPath = bundledPath;
    }

    public string? FindAria2(string? configuredPath)
    {
        // Only a full path: a relative one would be looked up in the current directory.
        if (!string.IsNullOrWhiteSpace(configuredPath) && Path.IsPathFullyQualified(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        return File.Exists(_bundledPath) ? _bundledPath : null;
    }
}
