using Colibri.Core.Platform;

namespace Colibri.Platform.Paths;

/// <summary>
/// Colibri's per-user files, under the local application data folder
/// (Windows: %LOCALAPPDATA%\Colibri, Linux: ~/.local/share/Colibri, macOS: ~/Library/Application Support/Colibri).
/// The data and log folders are created on construction.
/// </summary>
internal sealed class AppPaths : IAppPaths
{
    private readonly Lazy<string> _downloads;

    public AppPaths(IDownloadsFolderLocator downloadsFolder)
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Colibri"), downloadsFolder)
    {
    }

    internal AppPaths(string dataDirectory, IDownloadsFolderLocator downloadsFolder)
    {
        DataDirectory = dataDirectory;
        _downloads = new Lazy<string>(downloadsFolder.GetDownloadsFolder);
        Directory.CreateDirectory(DataDirectory);

        // Unix permission bits do not exist on Windows, where the access rights of LocalAppData
        // already keep other users out. (A guard around a Unix-only API, not a choice of implementation.)
        if (!OperatingSystem.IsWindows())
        {
            RestrictToOwner(DataDirectory);
        }

        Directory.CreateDirectory(LogsDirectory);
    }

    public string DataDirectory { get; }

    public string DatabasePath => Path.Combine(DataDirectory, "colibri.db");

    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public string Aria2SessionPath => Path.Combine(DataDirectory, "aria2.session");

    public string DefaultDownloadsDirectory => _downloads.Value;

    /// <summary>
    /// Owner-only (0700): the database and aria2's session file hold the cookies and auth headers of
    /// downloads, and other local users must not be able to read them. Applied on every start, so a
    /// folder created earlier with the default 0755 is tightened too.
    /// </summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void RestrictToOwner(string path) =>
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}
