using Colibri.Core.Models;

namespace Colibri.Core.Abstractions;

/// <summary>
/// Persistent storage for downloads.
/// </summary>
public interface IDownloadRepository
{
    /// <summary>Returns all stored downloads.</summary>
    Task<IReadOnlyList<DownloadItem>> GetAllAsync(CancellationToken ct);

    /// <summary>Returns one download, or null if it does not exist.</summary>
    Task<DownloadItem?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Stores a new download.</summary>
    Task AddAsync(DownloadItem item, CancellationToken ct);

    /// <summary>Saves changes to an existing download.</summary>
    Task UpdateAsync(DownloadItem item, CancellationToken ct);

    /// <summary>Deletes a download record (not the file).</summary>
    Task DeleteAsync(Guid id, CancellationToken ct);
}
