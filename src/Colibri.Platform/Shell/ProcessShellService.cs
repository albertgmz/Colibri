using System.Diagnostics;
using Colibri.Core.Platform;

namespace Colibri.Platform.Shell;

/// <summary>
/// Temporary <see cref="IShellService"/> for every OS: hands paths to the OS shell (Explorer, xdg-open,
/// open). "Reveal" opens the containing folder without selecting the file. To be replaced by per-OS
/// implementations.
/// </summary>
internal sealed class ProcessShellService : IShellService
{
    public Task OpenFileAsync(string path)
    {
        Launch(path);
        return Task.CompletedTask;
    }

    public Task RevealInFolderAsync(string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
        {
            Launch(folder);
        }

        return Task.CompletedTask;
    }

    public Task OpenFolderAsync(string path)
    {
        Launch(path);
        return Task.CompletedTask;
    }

    private static void Launch(string path)
    {
        // UseShellExecute lets the OS pick the default program for the file, or the file manager for a folder.
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
