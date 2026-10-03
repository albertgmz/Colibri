using Colibri.Core.Abstractions;
using Colibri.Core.Models;

namespace Colibri.Core.Tests.Fakes;

/// <summary>Repository that keeps copies of the items in memory and counts writes.</summary>
public sealed class InMemoryDownloadRepository : IDownloadRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DownloadItem> _items = new();

    public int UpdateCount { get; private set; }

    public InMemoryDownloadRepository(params DownloadItem[] seed)
    {
        foreach (var item in seed)
        {
            _items[item.Id] = item.Clone();
        }
    }

    /// <summary>A copy of the stored item, or null.</summary>
    public DownloadItem? Stored(Guid id)
    {
        lock (_gate)
        {
            return _items.GetValueOrDefault(id)?.Clone();
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public Task<IReadOnlyList<DownloadItem>> GetAllAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DownloadItem>>(_items.Values.Select(i => i.Clone()).ToList());
        }
    }

    public Task<DownloadItem?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Stored(id));

    public Task AddAsync(DownloadItem item, CancellationToken ct)
    {
        lock (_gate)
        {
            _items.Add(item.Id, item.Clone());
            return Task.CompletedTask;
        }
    }

    public Task UpdateAsync(DownloadItem item, CancellationToken ct)
    {
        lock (_gate)
        {
            UpdateCount++;
            _items[item.Id] = item.Clone();
            return Task.CompletedTask;
        }
    }

    public Task DeleteAsync(Guid id, CancellationToken ct)
    {
        lock (_gate)
        {
            _items.Remove(id);
            return Task.CompletedTask;
        }
    }
}
