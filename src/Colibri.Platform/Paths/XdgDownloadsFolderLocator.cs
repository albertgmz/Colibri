namespace Colibri.Platform.Paths;

/// <summary>
/// Linux: the XDG_DOWNLOAD_DIR entry of ~/.config/user-dirs.dirs (desktops write it, translated into the
/// user's language, e.g. "~/Descargas"), otherwise ~/Downloads.
/// </summary>
internal sealed class XdgDownloadsFolderLocator : IDownloadsFolderLocator
{
    public string GetDownloadsFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } configured
            ? configured
            : Path.Combine(home, ".config");
        var file = Path.Combine(configHome, "user-dirs.dirs");

        try
        {
            if (File.Exists(file) && ParseDownloadDir(File.ReadAllLines(file), home) is { } folder)
            {
                return folder;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable: use the default below.
        }

        return Path.Combine(home, "Downloads");
    }

    /// <summary>
    /// Reads XDG_DOWNLOAD_DIR from the lines of a user-dirs.dirs file. Values look like
    /// <c>"$HOME/Downloads"</c> or an absolute path. Returns null when the entry is missing or relative,
    /// or when it is the home folder itself (what xdg-user-dirs writes for a disabled folder).
    /// </summary>
    internal static string? ParseDownloadDir(IEnumerable<string> lines, string home)
    {
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("XDG_DOWNLOAD_DIR=", StringComparison.Ordinal))
            {
                continue;
            }

            var value = line["XDG_DOWNLOAD_DIR=".Length..].Trim().Trim('"');
            if (value.StartsWith("$HOME", StringComparison.Ordinal))
            {
                value = home.TrimEnd('/') + value["$HOME".Length..];
            }

            value = value.TrimEnd('/');
            if (!value.StartsWith('/') || value == home.TrimEnd('/'))
            {
                return null;
            }

            return value;
        }

        return null;
    }
}
