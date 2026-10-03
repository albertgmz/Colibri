using Colibri.Core.Models;

namespace Colibri.Core.Abstractions;

/// <summary>
/// Turns a link into one or more concrete downloads.
/// </summary>
public interface ILinkResolver
{
    /// <summary>Stable resolver id, used in logs.</summary>
    string Id { get; }

    /// <summary>Resolvers with a higher priority are asked first.</summary>
    int Priority { get; }

    /// <summary>
    /// Returns the downloads for <paramref name="url"/>, or null when this resolver does not handle it.
    /// </summary>
    Task<IReadOnlyList<DownloadRequest>?> ResolveAsync(Uri url, LinkContext context, CancellationToken ct);
}
