using Colibri.Core.Models;

namespace Colibri.Core.Services;

/// <summary>
/// Picks a <see cref="DownloadCategory"/> from a file name's extension.
/// </summary>
public static class CategoryMapper
{
    private static readonly Dictionary<string, DownloadCategory> ByExtension = BuildTable();

    /// <summary>
    /// Returns the category for <paramref name="fileName"/> (case-insensitive).
    /// The last extension decides; if it is unknown but the one before is <c>.tar</c> (".tar.lz4"),
    /// the result is <see cref="DownloadCategory.Compressed"/>. No or unknown extension is Other.
    /// </summary>
    public static DownloadCategory FromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return DownloadCategory.Other;
        }

        var parts = fileName.Split('.');
        if (parts.Length < 2)
        {
            return DownloadCategory.Other;
        }

        if (ByExtension.TryGetValue(parts[^1], out var category))
        {
            return category;
        }

        // Compressed tarballs whose compressor suffix we do not list, e.g. ".tar.lz4".
        if (parts.Length >= 3 && parts[^2].Equals("tar", StringComparison.OrdinalIgnoreCase))
        {
            return DownloadCategory.Compressed;
        }

        return DownloadCategory.Other;
    }

    private static Dictionary<string, DownloadCategory> BuildTable()
    {
        var table = new Dictionary<string, DownloadCategory>(StringComparer.OrdinalIgnoreCase);

        void Add(DownloadCategory category, params string[] extensions)
        {
            foreach (var extension in extensions)
            {
                table[extension] = category;
            }
        }

        Add(DownloadCategory.Compressed,
            "zip", "rar", "7z", "gz", "bz2", "xz", "tar", "tgz", "tbz2", "txz", "zst", "lz", "lzma", "cab", "iso");
        Add(DownloadCategory.Documents,
            "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "txt", "rtf", "epub", "mobi", "csv", "md");
        Add(DownloadCategory.Music,
            "mp3", "flac", "wav", "aac", "ogg", "oga", "m4a", "opus", "wma", "aiff", "alac", "mid", "midi");
        Add(DownloadCategory.Programs,
            "exe", "msi", "msix", "msixbundle", "appx", "dmg", "pkg", "deb", "rpm", "appimage", "apk", "flatpak", "snap", "run");
        Add(DownloadCategory.Video,
            "mp4", "mkv", "avi", "mov", "webm", "wmv", "flv", "m4v", "mpg", "mpeg", "ts", "3gp", "ogv", "vob");

        return table;
    }
}
