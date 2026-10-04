using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Colibri.Platform.Paths;

/// <summary>
/// The Downloads known folder. Users can move it (Properties > Location), so it is asked from the shell
/// instead of assuming "%USERPROFILE%\Downloads"; that path is only the fallback.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDownloadsFolderLocator : IDownloadsFolderLocator
{
    // FOLDERID_Downloads from KnownFolders.h.
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    public string GetDownloadsFolder()
    {
        var result = SHGetKnownFolderPath(DownloadsFolderId, 0, IntPtr.Zero, out var pathPointer);
        try
        {
            if (result == 0 && Marshal.PtrToStringUni(pathPointer) is { Length: > 0 } path)
            {
                return path;
            }
        }
        finally
        {
            // The shell allocates the string and the caller must free it, even when the call failed.
            Marshal.FreeCoTaskMem(pathPointer);
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid folderId, uint flags, IntPtr token, out IntPtr path);
}
