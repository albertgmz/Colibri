using Colibri.Core.Abstractions;
using Colibri.Core.Models;

namespace Colibri.Engine.Aria2.Smoke;

/// <summary>Memory-only smoke state. Real DownloadManager owns reconciliation; this is not a DB security test.</summary>
internal sealed class SmokeDownloadRepository : IDownloadRepository
{
    private readonly Dictionary<Guid, DownloadItem> _items = new();
    private readonly object _gate = new();

    public Task<IReadOnlyList<DownloadItem>> GetAllAsync(CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<DownloadItem>>(_items.Values.Select(i => i.Clone()).ToArray());
    }
    public Task<DownloadItem?> GetAsync(Guid id, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_items.GetValueOrDefault(id)?.Clone());
    }
    public Task AddAsync(DownloadItem item, CancellationToken ct)
    {
        lock (_gate) _items.Add(item.Id, item.Clone());
        return Task.CompletedTask;
    }
    public Task UpdateAsync(DownloadItem item, CancellationToken ct)
    {
        lock (_gate) _items[item.Id] = item.Clone();
        return Task.CompletedTask;
    }
    public Task DeleteAsync(Guid id, CancellationToken ct)
    {
        lock (_gate) _items.Remove(id);
        return Task.CompletedTask;
    }
}
