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

        foreach (var category in CaptureCatalog.Categories)
        {
            foreach (var extension in category.Extensions)
            {
                table.Add(extension, category.Category);
            }
        }

        return table;
    }
}
