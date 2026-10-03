using Colibri.Core.Models;

namespace Colibri.Core.Settings;

/// <summary>
/// User settings. The defaults here are what a fresh install uses.
/// </summary>
public sealed class AppSettings
{
    public WindowLayout Layout { get; set; } = new();

    public string AccentColor { get; set; } = "#C42B1C";

    /// <summary>Closing the main window hides it to the tray instead of exiting.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Minimizing the main window hides it to the tray.</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>Start Colibri when the user logs in.</summary>
    public bool StartWithSystem { get; set; }

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Whether the details pane under the download table is shown.</summary>
    public bool ShowDetailsPane { get; set; } = true;

    /// <summary>Base download folder. Empty means the user's Downloads folder, resolved at runtime.</summary>
    public string DefaultDownloadFolder { get; set; } = string.Empty;

    /// <summary>
    /// Folder per category. A missing or empty entry means
    /// <c>DefaultDownloadFolder/&lt;category name&gt;</c>.
    /// </summary>
    public Dictionary<DownloadCategory, string> CategoryFolders { get; set; } = new();

    /// <summary>How many downloads may transfer at the same time.</summary>
    public int MaxConcurrentDownloads { get; set; } = 3;

    /// <summary>Maximum connections to one server per download.</summary>
    public int ConnectionsPerServer { get; set; } = 16;

    /// <summary>Global download limit in KiB/s; 0 means unlimited.</summary>
    public int GlobalSpeedLimitKiB { get; set; }

    /// <summary>Path to the aria2c executable. Empty means the platform default.</summary>
    public string Aria2Path { get; set; } = string.Empty;

    /// <summary>File extensions (lower case, without the dot) that browser capture hands over to Colibri.</summary>
    public List<string> BrowserCaptureExtensions { get; set; } =
    [
        "zip", "rar", "7z", "gz", "tar",
        "exe", "msi", "dmg", "pkg", "deb", "rpm", "appimage", "iso",
        "pdf", "epub", "doc", "docx", "xls", "xlsx", "ppt", "pptx",
        "mp3", "flac", "wav",
        "mp4", "mkv", "avi", "mov", "webm",
    ];

    /// <summary>
    /// Browser downloads smaller than this (in KiB) stay in the browser.
    /// 0 captures every matching download, including ones whose size is unknown.
    /// </summary>
    public int BrowserCaptureMinSizeKiB { get; set; }
}
