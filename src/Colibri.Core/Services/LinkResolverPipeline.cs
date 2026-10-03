using Colibri.Core.Abstractions;
using Colibri.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Colibri.Core.Services;

/// <summary>
/// Asks link resolvers in priority order (highest first); the first non-null answer wins.
/// A resolver that throws is logged and skipped so it cannot block the others.
/// Every request a resolver returns is checked again: URLs that fail <see cref="UrlPolicy"/> are
/// dropped, and so are header values that are not valid HTTP.
/// </summary>
public sealed class LinkResolverPipeline
{
    private readonly IReadOnlyList<ILinkResolver> _resolvers;
    private readonly ILogger<LinkResolverPipeline> _logger;

    public LinkResolverPipeline(IEnumerable<ILinkResolver> resolvers, ILogger<LinkResolverPipeline>? logger = null)
    {
        // OrderByDescending is stable: resolvers with equal priority keep their registration order.
        _resolvers = resolvers.OrderByDescending(r => r.Priority).ToList();
        _logger = logger ?? NullLogger<LinkResolverPipeline>.Instance;
    }

    /// <summary>
    /// Returns the downloads for <paramref name="url"/>. An empty list means either that no resolver
    /// handles the link, or that the first resolver to handle it found nothing to download (an empty
    /// answer counts as "handled" and stops the chain).
    /// </summary>
    public async Task<IReadOnlyList<DownloadRequest>> ResolveAsync(Uri url, LinkContext context, CancellationToken ct)
    {
        foreach (var resolver in _resolvers)
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<DownloadRequest>? result;
            try
            {
                result = await resolver.ResolveAsync(url, context, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Link resolver {ResolverId} failed for {Url}; trying the next one",
                    resolver.Id, UrlPolicy.Redact(url));
                continue;
            }

            if (result is not null)
            {
                return Clean(result, resolver);
            }
        }

        return [];
    }

    private List<DownloadRequest> Clean(IReadOnlyList<DownloadRequest> requests, ILinkResolver resolver)
    {
        var cleaned = new List<DownloadRequest>(requests.Count);
        foreach (var request in requests)
        {
            if (!UrlPolicy.TryValidate(request.Uri.AbsoluteUri, out _, out var error))
            {
                _logger.LogWarning("Link resolver {ResolverId} returned a rejected URL {Url}: {Reason}",
                    resolver.Id, UrlPolicy.Redact(request.Uri), error);
                continue;
            }

            cleaned.Add(request with
            {
                Headers = HttpHeaders.CopyValid(request.Headers),
                Referrer = HttpHeaders.ValidValueOrNull(request.Referrer),
                UserAgent = HttpHeaders.ValidValueOrNull(request.UserAgent),
                Cookies = HttpHeaders.ValidValueOrNull(request.Cookies),
            });
        }

        return cleaned;
    }
}
