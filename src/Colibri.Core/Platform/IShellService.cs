namespace Colibri.Core.Platform;

/// <summary>
/// Opens files and folders with the operating system shell.
/// </summary>
public interface IShellService
{
    /// <summary>Opens a file with its default application.</summary>
    Task OpenFileAsync(string path);

    /// <summary>Opens the containing folder and selects the file where the OS supports it.</summary>
    Task RevealInFolderAsync(string path);

    /// <summary>Opens a folder in the file manager.</summary>
    Task OpenFolderAsync(string path);

    /// <summary>Opens a deliberate source-page re-association in the default browser.</summary>
    Task OpenUrlAsync(Uri uri) => throw new NotSupportedException();
}
