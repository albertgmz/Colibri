using Colibri.Core.Abstractions;
using Colibri.Core.Models;

namespace Colibri.Core.Services;

/// <summary>
/// Fallback resolver: downloads the URL as-is. Runs last and accepts every link.
/// Headers and header-like values that are not valid HTTP are dropped.
/// </summary>
public sealed class DirectLinkResolver : ILinkResolver
{
    public string Id => "direct";

    public int Priority => int.MinValue;

    public Task<IReadOnlyList<DownloadRequest>?> ResolveAsync(Uri url, LinkContext context, CancellationToken ct)
    {
        var request = new DownloadRequest
        {
            Uri = url,
            SuggestedFileName = string.IsNullOrWhiteSpace(context.FileName) ? FileNameFromUrl(url) : context.FileName,
            Headers = HttpHeaders.CopyValid(context.Headers),
            Referrer = HttpHeaders.ValidValueOrNull(context.Referrer),
            UserAgent = HttpHeaders.ValidValueOrNull(context.UserAgent),
            Cookies = HttpHeaders.ValidValueOrNull(context.Cookies),
            Size = context.Size,
            MimeType = context.MimeType,
        };

        return Task.FromResult<IReadOnlyList<DownloadRequest>?>([request]);
    }

    /// <summary>Last path segment, URL-decoded; null when the path ends with a slash.</summary>
    private static string? FileNameFromUrl(Uri url)
    {
        var lastSegment = url.Segments.Length > 0 ? url.Segments[^1] : string.Empty;
        if (lastSegment.Length == 0 || lastSegment.EndsWith('/'))
        {
            return null;
        }

        return Uri.UnescapeDataString(lastSegment);
    }
}
