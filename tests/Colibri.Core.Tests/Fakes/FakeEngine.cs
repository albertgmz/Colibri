using Colibri.Core.Engine;
using Colibri.Core.Models;

namespace Colibri.Core.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IDownloadEngine"/>: keeps download snapshots in a dictionary and records calls.
/// Tests change what it reports with <see cref="Report"/>. Also used by the App tests (linked source).
/// </summary>
public sealed class FakeEngine : IDownloadEngine
{
    private readonly object _gate = new();
    private readonly Dictionary<string, EngineDownloadStatus> _downloads = new();
    private int _nextHandle;

    public string Id => "fake";

    public EngineState State { get; private set; } = EngineState.Stopped;

    /// <summary>The state <see cref="StartAsync"/> ends in.</summary>
    public EngineState StateAfterStart { get; set; } = EngineState.Running;

    /// <summary>When set, <see cref="AddAsync"/> throws it.</summary>
    public Exception? AddFailure { get; set; }

    public List<AddCall> Adds { get; } = [];

    public List<string> Pauses { get; } = [];

    public List<string> Resumes { get; } = [];

    public List<string> Removes { get; } = [];

    public int StopCount { get; private set; }

    public event EventHandler<EngineDownloadEvent>? DownloadEvent;

    public event EventHandler<EngineState>? StateChanged;

    public bool CanHandle(DownloadRequest request) => request.Uri.Scheme is "http" or "https";

    public Task StartAsync(CancellationToken ct)
    {
        SetState(StateAfterStart);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        StopCount++;
        SetState(EngineState.Stopped);
        return Task.CompletedTask;
    }

    public Task ApplyOptionsAsync(EngineOptions options, CancellationToken ct) => Task.CompletedTask;

    public Task<string> AddAsync(DownloadRequest request, string saveFolder, string fileName, string? handle, bool startPaused, CancellationToken ct)
    {
        lock (_gate)
        {
            if (AddFailure is not null)
            {
                throw AddFailure;
            }

            handle ??= $"{++_nextHandle:x16}";
            if (_downloads.ContainsKey(handle))
            {
                throw new EngineOperationException($"Handle {handle} is already in use.");
            }

            Adds.Add(new AddCall(request, saveFolder, fileName, handle, startPaused));
            _downloads[handle] = new EngineDownloadStatus(handle, startPaused ? EngineDownloadState.Paused : EngineDownloadState.Waiting, 0, 0, 0, 0);
            return Task.FromResult(handle);
        }
    }

    public Task PauseAsync(string handle, CancellationToken ct) => Change(handle, EngineDownloadState.Paused, Pauses);

    public Task ResumeAsync(string handle, CancellationToken ct) => Change(handle, EngineDownloadState.Waiting, Resumes);

    public Task RemoveAsync(string handle, CancellationToken ct)
    {
        lock (_gate)
        {
            Removes.Add(handle);
            _downloads.Remove(handle);
            return Task.CompletedTask;
        }
    }

    public Task<EngineDownloadStatus?> GetStatusAsync(string handle, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_downloads.GetValueOrDefault(handle));
        }
    }

    /// <summary>Runs right after <see cref="GetAllAsync"/> took its snapshot (to simulate a race).</summary>
    public Action? AfterGetAll { get; set; }

    public Task<IReadOnlyList<EngineDownloadStatus>> GetAllAsync(CancellationToken ct)
    {
        List<EngineDownloadStatus> snapshot;
        lock (_gate)
        {
            snapshot = _downloads.Values.ToList();
        }

        AfterGetAll?.Invoke();
        return Task.FromResult<IReadOnlyList<EngineDownloadStatus>>(snapshot);
    }

    public Task<EngineGlobalStats> GetGlobalStatsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            var all = _downloads.Values.ToList();
            return Task.FromResult(new EngineGlobalStats(
                all.Sum(d => d.DownloadSpeed),
                all.Count(d => d.State == EngineDownloadState.Active),
                all.Count(d => d.State is EngineDownloadState.Waiting or EngineDownloadState.Paused)));
        }
    }

    /// <summary>Sets what the engine reports for <paramref name="handle"/> (adds it if unknown).</summary>
    public void Report(string handle, EngineDownloadState state, long total = 0, long completed = 0, long speed = 0, int connections = 0, string? error = null)
    {
        lock (_gate)
        {
            _downloads[handle] = new EngineDownloadStatus(handle, state, total, completed, speed, connections, error);
        }
    }

    public EngineDownloadStatus? Status(string handle)
    {
        lock (_gate)
        {
            return _downloads.GetValueOrDefault(handle);
        }
    }

    /// <summary>Forgets every download, like aria2 restarting without its session.</summary>
    public void Forget(string handle)
    {
        lock (_gate)
        {
            _downloads.Remove(handle);
        }
    }

    public void SetState(EngineState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public void RaiseDownloadEvent(string handle, EngineDownloadEventKind kind) =>
        DownloadEvent?.Invoke(this, new EngineDownloadEvent(handle, kind));

    private Task Change(string handle, EngineDownloadState state, List<string> calls)
    {
        lock (_gate)
        {
            calls.Add(handle);
            if (!_downloads.TryGetValue(handle, out var status))
            {
                throw new EngineOperationException($"Unknown handle {handle}.");
            }

            _downloads[handle] = status with { State = state, DownloadSpeed = 0 };
            return Task.CompletedTask;
        }
    }

    public sealed record AddCall(DownloadRequest Request, string SaveFolder, string FileName, string Handle, bool StartPaused);
}
