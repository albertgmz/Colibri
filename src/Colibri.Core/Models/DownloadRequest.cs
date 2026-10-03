namespace Colibri.Core.Models;

/// <summary>
/// A concrete file to download, produced by a link resolver.
/// </summary>
public sealed record DownloadRequest
{
    public required Uri Uri { get; init; }

    /// <summary>File name proposed by the source; not yet sanitized.</summary>
    public string? SuggestedFileName { get; init; }

    /// <summary>Extra request headers (case-insensitive names).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = HttpHeaders.Create();

    public string? Referrer { get; init; }

    public string? UserAgent { get; init; }

    /// <summary>Raw Cookie header value.</summary>
    public string? Cookies { get; init; }

    /// <summary>Expected size in bytes, when known.</summary>
    public long? Size { get; init; }

    public string? MimeType { get; init; }
}
