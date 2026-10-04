using Colibri.Core.Engine;
using Colibri.Core.Models;
using Colibri.Core.Network;
using Colibri.Core.Queues;

namespace Colibri.Core.Services;

public sealed partial class DownloadManager
{
    private readonly Dictionary<Guid, DownloadQueue> _queues = new();
    private EngineOptions _baseEngineOptions;
    private bool _queueOptionsChanged;

    private EngineOptions EffectiveEngineOptions() => _baseEngineOptions with
    {
        MaxConcurrentDownloads = Math.Max(1, _queues.Values.Where(q => q.IsRunning).Sum(q => q.MaxConcurrentDownloads)),
        // A positive budget is divided conservatively: a zero per-engine cap means unlimited.
        GlobalSpeedLimitBytesPerSecond = _baseEngineOptions.GlobalSpeedLimitBytesPerSecond > 0
            ? _baseEngineOptions.GlobalSpeedLimitBytesPerSecond / Math.Max(1, _engines.Count) : 0
    };

    private async Task ApplyQueueEngineOptionsAsync(CancellationToken ct)
    {
        foreach (var engine in _engines)
            await engine.ApplyOptionsAsync(EffectiveEngineOptions(), ct);
        _queueOptionsChanged = false;
    }
    public event EventHandler<IReadOnlyList<DownloadQueue>>? QueuesChanged;

    private QueueStore QueueStore => new(_paths);

    private async Task LoadQueuesAsync(CancellationToken ct)
    {
        var stored = await QueueStore.LoadAsync(ct);
        _queues.Clear();
        foreach (var queue in stored)
        {
            ValidateQueue(queue);
            if (!_queues.TryAdd(queue.Id, queue)) throw new InvalidDataException("Duplicate queue identifier.");
        }
        _queues.TryAdd(Guid.Empty, new DownloadQueue
        {
            MaxConcurrentDownloads = _settings.MaxConcurrentDownloads,
            LastScheduleEvaluationUtc = _time.GetUtcNow()
        });
        await EvaluateQueueSchedulesAsync(ct);
    }

    public Task<IReadOnlyList<DownloadQueue>> GetQueuesAsync(CancellationToken ct) =>
        RunLockedAsync<IReadOnlyList<DownloadQueue>>(_ => Task.FromResult<IReadOnlyList<DownloadQueue>>(
            _queues.Values.OrderBy(q => q.Id != Guid.Empty).ThenBy(q => q.Name).ToArray()), ct);

    public Task<DownloadQueue> CreateQueueAsync(string name, int maxConcurrent, CancellationToken ct) =>
        RunLockedAsync(async changes =>
        {
            var queue = new DownloadQueue { Id = Guid.NewGuid(), Name = name.Trim(),
                MaxConcurrentDownloads = maxConcurrent, IsRunning = false, LastScheduleEvaluationUtc = _time.GetUtcNow() };
            ValidateQueue(queue);
            await SaveQueueChangeAsync(queue, ct);
            return queue;
        }, ct);

    public Task UpdateQueueAsync(DownloadQueue queue, CancellationToken ct) => RunLockedAsync(async changes =>
    {
        if (!_queues.TryGetValue(queue.Id, out var existing)) throw new ArgumentException("Unknown queue.");
        queue = queue with { Name = queue.Name.Trim(), IsRunning = existing.IsRunning,
            LastScheduleEvaluationUtc = queue.Schedule == existing.Schedule ? existing.LastScheduleEvaluationUtc : _time.GetUtcNow() };
        ValidateQueue(queue);
        await SaveQueueChangeAsync(queue, ct);
        await EnforceQueuesAsync(changes, ct);
        if (_queueOptionsChanged) await ApplyQueueEngineOptionsAsync(ct);
        return true;
    }, ct);

    public Task SetQueueRunningAsync(Guid id, bool running, CancellationToken ct) => RunLockedAsync(async changes =>
    {
        if (!_queues.TryGetValue(id, out var queue)) throw new ArgumentException("Unknown queue.");
        var now = _time.GetUtcNow();
        await SaveQueueChangeAsync(queue with { IsRunning = running,
            LastScheduleEvaluationUtc = now > queue.LastScheduleEvaluationUtc ? now : queue.LastScheduleEvaluationUtc }, ct);
        await EnforceQueuesAsync(changes, ct);
        if (_queueOptionsChanged) await ApplyQueueEngineOptionsAsync(ct);
        return true;
    }, ct);

    public Task DeleteQueueAsync(Guid id, CancellationToken ct) => RunLockedAsync(async changes =>
    {
        if (id == Guid.Empty) throw new ArgumentException("The default queue cannot be deleted.");
        if (_items.Values.Any(i => i.QueueId == id)) throw new InvalidOperationException("Move the queue's downloads before deleting it.");
        var remaining = _queues.Values.Where(q => q.Id != id).ToArray();
        await QueueStore.SaveAsync(remaining, ct);
        _queues.Remove(id);
        await ApplyQueueEngineOptionsAsync(ct);
        Raise(QueuesChanged, remaining);
        return true;
    }, ct);

    public Task MoveToQueueAsync(IEnumerable<Guid> ids, Guid queueId, CancellationToken ct) => RunLockedAsync(async changes =>
    {
        if (!_queues.ContainsKey(queueId)) throw new ArgumentException("Unknown queue.");
        foreach (var item in Find(ids))
        {
            item.QueueId = queueId;
            await SaveAsync(item);
            changes.Update(item);
        }
        await EnforceQueuesAsync(changes, ct);
        if (_queueOptionsChanged) await ApplyQueueEngineOptionsAsync(ct);
        return true;
    }, ct);

    /// <summary>Changes routing only after the transfer is stopped; resuming remains an explicit user action.</summary>
    public Task SetNetworkPolicyAsync(Guid id, DownloadNetworkPolicy? policy, CancellationToken ct) => RunLockedAsync(async changes =>
    {
        policy?.Proxy?.Validate();
        if (!_items.TryGetValue(id, out var item) || item.State == DownloadState.Completed)
            throw new EngineOperationException("The download is unavailable.");
        await StopTransferForCancellationAsync(item, changes);
        if (EngineOf(item) is { State: EngineState.Running } engine && item.EngineHandle is { } handle)
            await engine.ApplyNetworkPolicyAsync(handle, policy, ct);
        item.NetworkPolicy = policy ?? _settings.DefaultNetworkPolicy ?? new DownloadNetworkPolicy();
        item.QueueHeld = false;
        await SaveAsync(item);
        changes.Update(item);
        if (_unsaved.Contains(item.Id))
            throw new IOException("The network policy could not be saved. The download remains paused.");
        return true;
    }, ct);

    private static void ValidateQueue(DownloadQueue queue)
    {
        if (string.IsNullOrWhiteSpace(queue.Name) || queue.Name.Length > 100)
            throw new ArgumentException("Queue names must contain between 1 and 100 characters.");
        if (queue.MaxConcurrentDownloads is < 1 or > 20)
            throw new ArgumentException("Queue concurrency must be between 1 and 20.");
        queue.Schedule?.Validate();
    }

    private async Task SaveQueueChangeAsync(DownloadQueue queue, CancellationToken ct)
    {
        if (_queues.Values.Any(q => q.Id != queue.Id && string.Equals(q.Name, queue.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A queue with that name already exists.");
        var next = _queues.Values.Where(q => q.Id != queue.Id).Append(queue).ToArray();
        await QueueStore.SaveAsync(next, ct);
        _queues[queue.Id] = queue;
        _queueOptionsChanged = true;
        Raise(QueuesChanged, next);
    }

    private async Task EvaluateQueueSchedulesAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        foreach (var queue in _queues.Values.ToArray())
        {
            if (queue.Schedule is not { } schedule) continue;
            if (QueueScheduleEvaluator.GetRunningState(schedule, queue.LastScheduleEvaluationUtc, now) is { } running)
                await SaveQueueChangeAsync(queue with { IsRunning = running, LastScheduleEvaluationUtc = now }, ct);
        }
    }

    private bool CanStartInQueue(DownloadItem item)
    {
        if (!_queues.TryGetValue(item.QueueId, out var queue)) return false;
        if (!queue.IsRunning) return false;
        var admitted = _items.Values.Count(i => i.Id != item.Id && i.QueueId == item.QueueId && !i.QueueHeld && !i.IsSeeding
            && i.State is DownloadState.Active or DownloadState.Queued);
        return admitted < queue.MaxConcurrentDownloads;
    }

    private async Task EnforceQueuesAsync(Changes changes, CancellationToken ct)
    {
        foreach (var group in _items.Values.Where(i => i.State is DownloadState.Active or DownloadState.Queued)
            .GroupBy(i => i.QueueId).ToArray())
        {
            var limit = _queues.TryGetValue(group.Key, out var queue) && queue.IsRunning ? queue.MaxConcurrentDownloads : 0;
            // Retain existing admissions, then use stable FIFO order for newly available slots.
            var items = group.OrderBy(i => i.QueueHeld).ThenBy(i => i.AddedAt).ThenBy(i => i.Id).ToArray();
            var admittedContent = 0;
            for (var index = 0; index < items.Length; index++)
            {
                var item = items[index];
                var hold = limit == 0 || (!item.IsSeeding && admittedContent++ >= limit);
                var engine = EngineOf(item);
                if (hold)
                {
                    if (engine is { State: EngineState.Running } && item.EngineHandle is { } handle)
                    {
                        var status = await engine.GetStatusAsync(handle, ct);
                        if (!item.QueueHeld || status?.State is EngineDownloadState.Active or EngineDownloadState.Waiting)
                        {
                            // Keep unexpected live engine state visible if all stop methods fail.
                            if (status is not null && ApplyStatus(item, status, out _))
                            {
                                changes.Update(item);
                                await SaveAsync(item);
                            }
                            await StopTransferForCancellationAsync(item, changes);
                            // Cleanup clears holds for browser rollback; this stop belongs to queue policy.
                            item.QueueHeld = true;
                            TrySetState(item, DownloadState.Queued);
                            await SaveAsync(item);
                            changes.Update(item);
                        }
                    }
                    if (!item.QueueHeld || item.State == DownloadState.Active)
                    {
                        item.QueueHeld = true;
                        TrySetState(item, DownloadState.Queued);
                        item.DownloadSpeed = 0;
                        item.Connections = 0;
                        await SaveAsync(item);
                        changes.Update(item);
                    }
                }
                else if (item.QueueHeld && engine is { State: EngineState.Running })
                {
                    item.QueueHeld = false;
                    await ResumeOneAsync(item, changes, ct);
                    await SaveAsync(item);
                    changes.Update(item);
                }
            }
        }
    }
}
