using Microsoft.Extensions.Logging;

namespace Colibri.Platform.Shell;

/// <summary>
/// macOS: the <c>open</c> command opens files with their default app and folders in Finder;
/// <c>open -R</c> reveals (selects) the item in Finder.
/// </summary>
internal sealed class MacShellService(ILogger<MacShellService> logger) : ShellServiceBase(logger)
{
    protected override Task OpenAsync(string path)
    {
        ProcessLauncher.TryStart("open", [path], Logger);
        return Task.CompletedTask;
    }

    protected override Task SelectAsync(string path)
    {
        ProcessLauncher.TryStart("open", ["-R", path], Logger);
        return Task.CompletedTask;
    }
}
