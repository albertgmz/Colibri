using Colibri.Core.Platform;
using Microsoft.Extensions.Logging;

namespace Colibri.Platform.Shell;

/// <summary>
/// Rules shared by every OS: missing files and folders are logged instead of reported as errors, "show in
/// folder" for a file that no longer exists opens its folder when that still exists, and failures of the
/// OS-specific part are logged, never thrown. Paths are made absolute first, so a name starting with '-'
/// can never be taken for an option by the helper programs.
/// </summary>
internal abstract class ShellServiceBase(ILogger logger) : IShellService
{
    protected ILogger Logger { get; } = logger;

    public Task OpenFileAsync(string path)
    {
        if (FullPath(path) is not { } file || !File.Exists(file))
        {
            Logger.LogWarning("Cannot open {Path}: the file does not exist", path);
            return Task.CompletedTask;
        }

        return RunAsync(() => OpenAsync(file), file);
    }

    public Task OpenFolderAsync(string path)
    {
        if (FullPath(path) is not { } folder || !Directory.Exists(folder))
        {
            Logger.LogWarning("Cannot open {Path}: the folder does not exist", path);
            return Task.CompletedTask;
        }

        return RunAsync(() => OpenAsync(folder), folder);
    }

    public Task RevealInFolderAsync(string path)
    {
        var item = FullPath(path);
        if (item is not null && (File.Exists(item) || Directory.Exists(item)))
        {
            return RunAsync(() => SelectAsync(item), item);
        }

        if (item is not null && Path.GetDirectoryName(item) is { Length: > 0 } folder && Directory.Exists(folder))
        {
            return RunAsync(() => OpenAsync(folder), folder);
        }

        Logger.LogWarning("Cannot show {Path}: neither the file nor its folder exists", path);
        return Task.CompletedTask;
    }

    /// <summary>Opens a file with its default program, or a folder in the file manager.</summary>
    protected abstract Task OpenAsync(string path);

    /// <summary>Opens the folder that contains <paramref name="path"/> with that item selected.</summary>
    protected abstract Task SelectAsync(string path);

    /// <summary>The absolute path, or null for an empty or invalid one (such as one containing '\0').</summary>
    private static string? FullPath(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task RunAsync(Func<Task> action, string path)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "The shell could not open {Path}", path);
        }
    }
}
