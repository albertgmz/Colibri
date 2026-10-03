namespace Colibri.Core.Models;

/// <summary>
/// What is known about a link besides its URL (usually supplied by the browser).
/// </summary>
public sealed record LinkContext
{
    /// <summary>A context with no extra information.</summary>
    public static LinkContext Empty { get; } = new();

    public string? Referrer { get; init; }

    /// <summary>Raw Cookie header value.</summary>
    public string? Cookies { get; init; }

    /// <summary>Extra request headers (case-insensitive names).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = HttpHeaders.Create();

    public string? UserAgent { get; init; }

    /// <summary>File name proposed by the browser; not yet sanitized.</summary>
    public string? FileName { get; init; }

    /// <summary>Expected size in bytes, when known.</summary>
    public long? Size { get; init; }

    public string? MimeType { get; init; }
}
