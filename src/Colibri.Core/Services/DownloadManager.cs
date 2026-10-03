using Colibri.Core.Abstractions;
using Colibri.Core.Engine;
using Colibri.Core.Models;
using Colibri.Core.Platform;
using Colibri.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Colibri.Core.Services;

/// <summary>
/// The download use cases: add, pause, resume, delete, keep the list in step with the engine, and
/// save it. Holds the in-memory list of downloads; the database is the truth on disk and is loaded
/// once at startup.
/// </summary>
/// <remarks>
/// Events are raised on background threads; UI code must marshal them to its own thread. Items passed
/// to event handlers and returned from methods are copies, so they can be read freely.
/// </remarks>
public sealed class DownloadManager
{
    /// <summary>Progress-only changes are written to the database at most this often per download.</summary>
    public static readonly TimeSpan ProgressSaveInterval = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<IDownloadEngine> _engines;
    private readonly IDownloadRepository _repository;
    private readonly LinkResolverPipeline _resolvers;
    private readonly AppSettings _settings;
    private readonly IAppPaths _paths;
    private readonly ILogger<DownloadManager> _logger;
    private readonly TimeProvider _time;

    // Every operation that reads or changes the downloads runs under this lock, one at a time.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, DownloadItem> _items = new();
    private readonly Dictionary<Guid, DateTimeOffset> _lastSaved = new();
    private readonly HashSet<Guid> _unsaved = new();

    private readonly object _intervalLock = new();
    private TimeSpan _pollInterval = TimeSpan.FromSeconds(1);
    private CancellationTokenSource _intervalChanged = new();

    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private volatile bool _initialized;

    public DownloadManager(
        IEnumerable<IDownloadEngine> engines,
        IDownloadRepository repository,
        LinkResolverPipeline resolvers,
        AppSettings settings,
        IAppPaths paths,
        ILogger<DownloadManager> logger,
        TimeProvider time)
    {
        _engines = engines.ToList();
        _repository = repository;
        _resolvers = resolvers;
        _settings = settings;
        _paths = paths;
        _logger = logger;
        _time = time;
    }

    /// <summary>A download was added.</summary>
    public event EventHandler<DownloadItem>? ItemAdded;

    /// <summary>A download was deleted.</summary>
    public event EventHandler<Guid>? ItemRemoved;

    /// <summary>Downloads changed (progress, state, ...); batched per poll tick or operation.</summary>
    public event EventHandler<IReadOnlyList<DownloadItem>>? ItemsUpdated;

    /// <summary>Totals across all engines, raised on every poll tick.</summary>
    public event EventHandler<EngineGlobalStats>? GlobalStatsChanged;

    /// <summary>The engine's health changed.</summary>
    public event EventHandler<EngineState>? EngineStateChanged;

    /// <summary>Health of the engine (v1 has a single engine, aria2).</summary>
    public EngineState EngineState => _engines.Count > 0 ? _engines[0].State : EngineState.NotFound;

    /// <summary>
    /// Loads the stored downloads, starts the engines, brings them in step with the database and
    /// starts polling. Call once.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        var stored = await _repository.GetAllAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var item in stored)
            {
                // Speed and connections only mean something while the engine reports them.
                item.DownloadSpeed = 0;
                item.Connections = 0;
                // TryAdd: a download added while the list was loading is newer than its stored copy.
                if (_items.TryAdd(item.Id, item))
                {
                    _lastSaved[item.Id] = _time.GetUtcNow();
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        _logger.LogInformation("Loaded {Count} downloads", stored.Count);

        foreach (var engine in _engines)
        {
            engine.StateChanged += OnEngineStateChanged;
            engine.DownloadEvent += OnEngineDownloadEvent;
        }

        foreach (var engine in _engines)
        {
            try
            {
                await engine.StartAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Engine {EngineId} failed to start", engine.Id);
            }

            if (engine.State == EngineState.Running)
            {
                await ReconcileAsync(engine, ct);
            }
        }

        // From now on, an engine coming back to Running (after a restart or a retry) is reconciled again.
        _initialized = true;

        _pollCts = new CancellationTokenSource();
        var pollToken = _pollCts.Token;
        _pollTask = Task.Run(() => PollLoopAsync(pollToken), CancellationToken.None);
    }

    /// <summary>
    /// Stops polling, writes progress that was not saved yet and stops the engines (aria2 saves its
    /// session, so unfinished downloads continue on the next start).
    /// </summary>
    public async Task StopAsync()
    {
        if (_pollCts is not null)
        {
            await _pollCts.CancelAsync();
            try
            {
                await (_pollTask ?? Task.CompletedTask);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        await RunLockedAsync(async _ =>
        {
            foreach (var id in _unsaved.ToList())
            {
                if (_items.TryGetValue(id, out var item))
                {
                    await SaveAsync(item);
                }
            }

            return true;
        }, CancellationToken.None);

        foreach (var engine in _engines)
        {
            engine.StateChanged -= OnEngineStateChanged;
            engine.DownloadEvent -= OnEngineDownloadEvent;
            try
            {
                await engine.StopAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Engine {EngineId} did not stop cleanly", engine.Id);
            }
        }
    }

    /// <summary>
    /// How often the engine is asked for progress. The app uses a short interval while the window is
    /// visible and a longer one while it is hidden. A change takes effect at once.
    /// </summary>
    public void SetPollingInterval(TimeSpan interval)
    {
        CancellationTokenSource previous;
        lock (_intervalLock)
        {
            if (_pollInterval == interval)
            {
                return;
            }

            _pollInterval = interval;
            previous = _intervalChanged;
            _intervalChanged = new CancellationTokenSource();
        }

        // Wakes the poll loop so the new interval applies now, not after the old one ends.
        previous.Cancel();
    }

    /// <summary>Starts engines that are not running (for example after aria2 was installed).</summary>
    public async Task RetryEngineAsync(CancellationToken ct)
    {
        foreach (var engine in _engines.Where(e => e.State is EngineState.NotFound or EngineState.Failed or EngineState.Stopped))
        {
            try
            {
                await engine.StartAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Engine {EngineId} failed to start", engine.Id);
            }
        }
    }

    /// <summary>Copies of all downloads.</summary>
    public Task<IReadOnlyList<DownloadItem>> GetItemsAsync(CancellationToken ct) =>
        RunLockedAsync<IReadOnlyList<DownloadItem>>(_ => Task.FromResult<IReadOnlyList<DownloadItem>>(
            _items.Values.Select(i => i.Clone()).ToList()), ct);

    /// <summary>
    /// The folder a new download of <paramref name="category"/> goes to: the category's folder from the
    /// settings, otherwise "&lt;default folder&gt;/&lt;category name&gt;".
    /// </summary>
    public string GetCategoryFolder(DownloadCategory category)
    {
        if (_settings.CategoryFolders.TryGetValue(category, out var folder) && !string.IsNullOrWhiteSpace(folder))
        {
            return folder;
        }

        var root = string.IsNullOrWhiteSpace(_settings.DefaultDownloadFolder)
            ? _paths.DefaultDownloadsDirectory
            : _settings.DefaultDownloadFolder;
        return Path.Combine(root, category.ToString());
    }

    /// <summary>
    /// Adds the downloads behind <paramref name="url"/> and returns them. A download the engine refuses
    /// is still added, in the Failed state with the reason.
    /// </summary>
    /// <param name="url">The link.</param>
    /// <param name="context">What the browser (or the user) knows about the link.</param>
    /// <param name="fileNameOverride">File name chosen by the user; used when the link is a single file.</param>
    /// <param name="folderOverride">Folder chosen by the user; null means the category folder.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">The URL is not accepted by <see cref="UrlPolicy"/>.</exception>
    public async Task<IReadOnlyList<DownloadItem>> AddAsync(
        string url, LinkContext context, string? fileNameOverride, string? folderOverride, CancellationToken ct)
    {
        if (!UrlPolicy.TryValidate(url, out var uri, out string? error))
        {
            throw new ArgumentException(error, nameof(url));
        }

        // A relative folder would be resolved against the current directory, which differs between
        // Colibri and aria2; Open and Delete with file would then point somewhere else.
        if (!string.IsNullOrWhiteSpace(folderOverride) && !Path.IsPathFullyQualified(folderOverride))
        {
            throw new ArgumentException($"The folder '{folderOverride}' is not a full path.", nameof(folderOverride));
        }

        var requests = await _resolvers.ResolveAsync(uri, context, ct);
        if (requests.Count == 0)
        {
            _logger.LogWarning("Nothing to download at {Url}", UrlPolicy.Redact(uri));
        }

        return await RunLockedAsync<IReadOnlyList<DownloadItem>>(async changes =>
        {
            var created = new List<DownloadItem>();
            foreach (var request in requests)
            {
                var nameOverride = requests.Count == 1 ? fileNameOverride : null;
                var item = await AddOneAsync(request, nameOverride, folderOverride, changes, ct);
                created.Add(item.Clone());
            }

            return created;
        }, ct);
    }

    /// <summary>Pauses the given downloads that are queued or active.</summary>
    public Task PauseAsync(IEnumerable<Guid> ids, CancellationToken ct) =>
        RunLockedAsync(changes => PauseItemsAsync(Find(ids), changes, ct), ct);

    /// <summary>
    /// Resumes paused downloads and retries failed ones. A download the engine no longer knows is added
    /// again with the same handle, folder and name, so the engine continues the partial file.
    /// </summary>
    public Task ResumeAsync(IEnumerable<Guid> ids, CancellationToken ct) =>
        RunLockedAsync(changes => ResumeItemsAsync(Find(ids), changes, ct), ct);

    /// <summary>Pauses every queued or active download.</summary>
    public Task PauseAllAsync(CancellationToken ct) =>
        RunLockedAsync(changes => PauseItemsAsync(_items.Values.ToList(), changes, ct), ct);

    /// <summary>Resumes every paused download (failed ones are only retried on request).</summary>
    public Task ResumeAllAsync(CancellationToken ct) =>
        RunLockedAsync(changes => ResumeItemsAsync(_items.Values.Where(i => i.State == DownloadState.Paused).ToList(), changes, ct), ct);

    /// <summary>Retries a failed download.</summary>
    public Task RetryAsync(Guid id, CancellationToken ct) => ResumeAsync([id], ct);

    /// <summary>
    /// Deletes downloads: removes them from the engine and the database and, when
    /// <paramref name="deleteFiles"/> is true, deletes the file and its ".aria2" control file.
    /// </summary>
    public Task DeleteAsync(IEnumerable<Guid> ids, bool deleteFiles, CancellationToken ct) =>
        RunLockedAsync(async changes =>
        {
            foreach (var item in Find(ids))
            {
                var engine = EngineOf(item);
                if (engine is { State: EngineState.Running } && item.EngineHandle is { } handle)
                {
                    // Waits until the engine has stopped writing, so the file can be deleted below.
                    await TryEngineAsync(() => engine.RemoveAsync(handle, ct), "remove", item);
                }

                var path = Path.Combine(item.SaveFolder, item.FileName);
                if (deleteFiles && _items.Values.Any(other => other.Id != item.Id && IsSamePath(other, path)))
                {
                    // Rows from before names were reserved may share a path; never delete another row's file.
                    _logger.LogWarning("Not deleting {Path}: another download uses the same file", path);
                }
                else if (deleteFiles)
                {
                    DeleteFile(path);
                    DeleteFile(path + ".aria2");
                }

                try
                {
                    await _repository.DeleteAsync(item.Id, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not delete download {Id} from the database", item.Id);
                }

                _items.Remove(item.Id);
                _lastSaved.Remove(item.Id);
                _unsaved.Remove(item.Id);
                changes.Removed.Add(item.Id);
            }

            return true;
        }, ct);

    /// <summary>One poll tick: reads every download and the totals from each running engine.</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        long speed = 0;
        int active = 0, waiting = 0;
        foreach (var engine in _engines.Where(e => e.State == EngineState.Running))
        {
            // The engine is read while holding the lock: a snapshot taken before a pause or resume that
            // runs meanwhile would otherwise be applied after it and undo it.
            var stats = await RunLockedAsync<EngineGlobalStats?>(async changes =>
            {
                IReadOnlyList<EngineDownloadStatus> statuses;
                EngineGlobalStats engineStats;
                try
                {
                    // One request for all downloads, plus the totals.
                    statuses = await engine.GetAllAsync(ct);
                    engineStats = await engine.GetGlobalStatsAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The engine may be restarting; the next tick tries again.
                    _logger.LogDebug(ex, "Polling engine {EngineId} failed", engine.Id);
                    return null;
                }

                var byHandle = ToDictionary(statuses);
                var now = _time.GetUtcNow();
                foreach (var item in _items.Values.Where(i => i.EngineId == engine.Id && i.EngineHandle is not null))
                {
                    if (!byHandle.TryGetValue(item.EngineHandle!, out var status)
                        || !ApplyStatus(item, status, out var stateChanged))
                    {
                        continue;
                    }

                    changes.Update(item);
                    var lastSaved = _lastSaved.GetValueOrDefault(item.Id, DateTimeOffset.MinValue);
                    if (stateChanged || now - lastSaved >= ProgressSaveInterval)
                    {
                        await SaveAsync(item);
                    }
                    else
                    {
                        _unsaved.Add(item.Id);
                    }
                }

                return engineStats;
            }, ct);

            if (stats is not null)
            {
                speed += stats.DownloadSpeed;
                active += stats.NumActive;
                waiting += stats.NumWaiting;
            }
        }

        Raise(GlobalStatsChanged, new EngineGlobalStats(speed, active, waiting));
    }

    /// <summary>
    /// Brings the database and an engine in step. For each unfinished download: if the engine knows it,
    /// its state is adopted, except that the user's choice wins (paused in Colibri but running in the
    /// engine -> paused in the engine, and the other way round). If the engine does not know it, it is
    /// added again with the same handle, folder and name (paused if it was paused), and the engine
    /// resumes the partial file. Failed and completed downloads are left alone; so are engine downloads
    /// that are not in the database.
    /// </summary>
    internal async Task ReconcileAsync(IDownloadEngine engine, CancellationToken ct)
    {
        await RunLockedAsync(async changes =>
        {
            // Read under the lock, so a download added meanwhile is not mistaken for an unknown one.
            IReadOnlyList<EngineDownloadStatus> statuses;
            try
            {
                statuses = await engine.GetAllAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Could not read the downloads of engine {EngineId}; skipping reconcile", engine.Id);
                return false;
            }

            var known = ToDictionary(statuses);
            foreach (var item in _items.Values.Where(i => i.EngineId == engine.Id && i.State != DownloadState.Completed).ToList())
            {
                await ReconcileOneAsync(engine, item, known, changes, ct);
            }

            var ours = _items.Values.Select(i => i.EngineHandle).OfType<string>().ToHashSet();
            foreach (var status in statuses.Where(s => !ours.Contains(s.Handle)))
            {
                _logger.LogInformation("Engine {EngineId} has download {Handle} that is not in the database; leaving it alone",
                    engine.Id, status.Handle);
            }

            return true;
        }, ct);
    }

    private async Task ReconcileOneAsync(
        IDownloadEngine engine, DownloadItem item, Dictionary<string, EngineDownloadStatus> known, Changes changes, CancellationToken ct)
    {
        if (item.EngineHandle is { } handle && known.TryGetValue(handle, out var status) && status.State != EngineDownloadState.Removed)
        {
            if (item.State == DownloadState.Paused && status.State is EngineDownloadState.Active or EngineDownloadState.Waiting)
            {
                if (await TryEngineAsync(() => engine.PauseAsync(handle, ct), "pause", item))
                {
                    status = status with { State = EngineDownloadState.Paused, DownloadSpeed = 0 };
                }
            }
            else if (item.State is DownloadState.Active or DownloadState.Queued && status.State == EngineDownloadState.Paused)
            {
                if (await TryEngineAsync(() => engine.ResumeAsync(handle, ct), "resume", item))
                {
                    status = status with { State = EngineDownloadState.Waiting };
                }
            }

            if (ApplyStatus(item, status, out _))
            {
                changes.Update(item);
                await SaveAsync(item);
            }

            return;
        }

        if (item.State == DownloadState.Failed)
        {
            return; // Retried only when the user asks.
        }

        if (item.EngineHandle is { } removedHandle && known.ContainsKey(removedHandle))
        {
            // The engine still holds the result of a removed download under this handle; clear it so the
            // handle can be used again.
            await TryEngineAsync(() => engine.RemoveAsync(removedHandle, ct), "clear", item);
        }

        _logger.LogInformation("Re-adding download {Id} ({FileName}) to engine {EngineId}", item.Id, item.FileName, engine.Id);
        await AddToEngineAsync(engine, item, startPaused: item.State == DownloadState.Paused, changes, ct);
    }

    private async Task<bool> PauseItemsAsync(List<DownloadItem> items, Changes changes, CancellationToken ct)
    {
        foreach (var item in items.Where(i => i.State is DownloadState.Active or DownloadState.Queued))
        {
            var engine = EngineOf(item);
            if (engine is { State: EngineState.Running } && item.EngineHandle is { } handle)
            {
                await TryEngineAsync(() => engine.PauseAsync(handle, ct), "pause", item);
            }

            if (TrySetState(item, DownloadState.Paused))
            {
                item.DownloadSpeed = 0;
                item.Connections = 0;
                changes.Update(item);
                await SaveAsync(item);
            }
        }

        return true;
    }

    private async Task<bool> ResumeItemsAsync(List<DownloadItem> items, Changes changes, CancellationToken ct)
    {
        foreach (var item in items.Where(i => i.State is DownloadState.Paused or DownloadState.Failed))
        {
            await ResumeOneAsync(item, changes, ct);
        }

        return true;
    }

    private async Task<DownloadItem> AddOneAsync(
        DownloadRequest request, string? fileNameOverride, string? folderOverride, Changes changes, CancellationToken ct)
    {
        var name = FileNameSanitizer.Sanitize(string.IsNullOrWhiteSpace(fileNameOverride) ? request.SuggestedFileName : fileNameOverride);
        var category = CategoryMapper.FromFileName(name);
        var folder = string.IsNullOrWhiteSpace(folderOverride) ? GetCategoryFolder(category) : folderOverride;
        var engine = _engines.FirstOrDefault(e => e.CanHandle(request));

        var headers = HttpHeaders.Copy(request.Headers);
        if (request.Cookies is not null)
        {
            // Kept with the headers so a retry or a re-add after a restart sends the same cookies.
            headers["Cookie"] = request.Cookies;
        }

        var item = new DownloadItem
        {
            Url = request.Uri.AbsoluteUri,
            FileName = name,
            SaveFolder = folder,
            Category = category,
            State = DownloadState.Queued,
            TotalBytes = request.Size,
            EngineId = engine?.Id ?? string.Empty,
            Referrer = request.Referrer,
            UserAgent = request.UserAgent,
            Headers = headers,
            AddedAt = _time.GetUtcNow(),
        };

        string? problem = null;
        try
        {
            Directory.CreateDirectory(folder);
            item.FileName = FileNameSanitizer.MakeUnique(folder, name, IsPathTaken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            problem = ex.Message;
        }

        if (engine is null)
        {
            problem ??= $"No download engine can handle '{request.Uri.Scheme}' links.";
        }

        if (problem is not null)
        {
            _logger.LogWarning("Download {FileName} from {Url} cannot start: {Problem}", item.FileName, UrlPolicy.Redact(request.Uri), problem);
            item.State = DownloadState.Failed;
            item.ErrorMessage = problem;
        }

        await _repository.AddAsync(item, CancellationToken.None);
        _items[item.Id] = item;
        _lastSaved[item.Id] = _time.GetUtcNow();
        changes.Added.Add(item);
        _logger.LogInformation("Added download {Id} ({FileName}) from {Url}", item.Id, item.FileName, UrlPolicy.Redact(request.Uri));

        if (problem is null && engine is not null)
        {
            await AddToEngineAsync(engine, item, startPaused: false, changes, ct);
        }

        return item;
    }

    private async Task ResumeOneAsync(DownloadItem item, Changes changes, CancellationToken ct)
    {
        var engine = EngineOf(item);
        if (engine is null)
        {
            _logger.LogWarning("Download {Id} belongs to unknown engine '{EngineId}'", item.Id, item.EngineId);
            return;
        }

        if (engine.State != EngineState.Running)
        {
            if (item.State == DownloadState.Failed)
            {
                // A retry needs the engine: aria2 may still hold the failed result and would report it
                // again when it starts. The user can retry once the engine runs.
                _logger.LogInformation("Not retrying download {Id}: the engine is not running", item.Id);
                return;
            }

            // Queued for now; reconcile resumes it as soon as the engine runs.
            if (TrySetState(item, DownloadState.Queued))
            {
                item.ErrorMessage = null;
                changes.Update(item);
                await SaveAsync(item);
            }

            return;
        }

        EngineDownloadStatus? status = null;
        if (item.EngineHandle is { } handle)
        {
            try
            {
                status = await engine.GetStatusAsync(handle, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not read download {Id} from the engine", item.Id);
            }

            switch (status?.State)
            {
                case EngineDownloadState.Paused:
                    if (await TryEngineAsync(() => engine.ResumeAsync(handle, ct), "resume", item) && TrySetState(item, DownloadState.Queued))
                    {
                        changes.Update(item);
                        await SaveAsync(item);
                    }

                    return;

                case EngineDownloadState.Active or EngineDownloadState.Waiting:
                    // Our pause never reached the engine; it is still running.
                    if (TrySetState(item, DownloadState.Queued))
                    {
                        changes.Update(item);
                        await SaveAsync(item);
                    }

                    return;

                case EngineDownloadState.Complete:
                    if (ApplyStatus(item, status, out _))
                    {
                        changes.Update(item);
                        await SaveAsync(item);
                    }

                    return;

                case not null:
                    // Error or removed: the engine keeps the result under this handle; clear it so the
                    // download can be added again with the same handle.
                    await TryEngineAsync(() => engine.RemoveAsync(handle, ct), "clear", item);
                    break;
            }
        }

        await AddToEngineAsync(engine, item, startPaused: false, changes, ct);
    }

    /// <summary>
    /// Adds an item to the engine (again), reusing its handle if it has one. On failure the item becomes
    /// Failed with the engine's message. While the engine is not running nothing happens: reconcile
    /// adds the item once it runs.
    /// </summary>
    private async Task AddToEngineAsync(IDownloadEngine engine, DownloadItem item, bool startPaused, Changes changes, CancellationToken ct)
    {
        if (engine.State != EngineState.Running)
        {
            return;
        }

        try
        {
            item.EngineHandle = await engine.AddAsync(ToRequest(item), item.SaveFolder, item.FileName, item.EngineHandle, startPaused, ct);
            item.ErrorMessage = null;
            if (!startPaused)
            {
                TrySetState(item, DownloadState.Queued);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Engine {EngineId} refused download {Id} ({FileName})", engine.Id, item.Id, item.FileName);
            if (TrySetState(item, DownloadState.Failed))
            {
                item.ErrorMessage = ex.Message;
            }
        }

        changes.Update(item);
        await SaveAsync(item);
    }

    /// <summary>
    /// Copies an engine snapshot into <paramref name="item"/>. Returns whether anything changed;
    /// <paramref name="stateChanged"/> tells whether the state did. Illegal state changes are logged
    /// and ignored.
    /// </summary>
    private bool ApplyStatus(DownloadItem item, EngineDownloadStatus status, out bool stateChanged)
    {
        stateChanged = false;
        if (item.State == DownloadState.Completed)
        {
            return false;
        }

        var changed = false;
        var target = ToDownloadState(status.State);

        // aria2's forcePause takes a moment; until then it still reports the download as active. The
        // user's Paused stands until they resume (reconcile fixes a real mismatch after a restart).
        if (item.State == DownloadState.Paused && target is DownloadState.Active or DownloadState.Queued)
        {
            target = null;
        }

        if (target is { } newState && TrySetState(item, newState))
        {
            stateChanged = changed = true;
            switch (newState)
            {
                case DownloadState.Completed:
                    item.CompletedAt = _time.GetUtcNow();
                    break;
                case DownloadState.Failed:
                    item.ErrorMessage = status.ErrorMessage;
                    break;
                case DownloadState.Active or DownloadState.Queued:
                    item.ErrorMessage = null;
                    break;
            }
        }

        // A paused download restored from aria2's session reports 0 of 0 bytes until it is resumed;
        // keep the numbers we already have.
        if (status.TotalBytes > 0 && item.TotalBytes != status.TotalBytes)
        {
            item.TotalBytes = status.TotalBytes;
            changed = true;
        }

        if ((status.TotalBytes > 0 || status.CompletedBytes > 0) && item.CompletedBytes != status.CompletedBytes)
        {
            item.CompletedBytes = status.CompletedBytes;
            changed = true;
        }

        if (item.State == DownloadState.Completed && item.TotalBytes is { } total && item.CompletedBytes != total)
        {
            item.CompletedBytes = total;
            changed = true;
        }

        var running = item.State == DownloadState.Active;
        var speed = running ? status.DownloadSpeed : 0;
        var connections = running ? status.Connections : 0;
        if (item.DownloadSpeed != speed || item.Connections != connections)
        {
            item.DownloadSpeed = speed;
            item.Connections = connections;
            changed = true;
        }

        return changed;
    }

    private static DownloadState? ToDownloadState(EngineDownloadState state) => state switch
    {
        EngineDownloadState.Active => DownloadState.Active,
        EngineDownloadState.Waiting => DownloadState.Queued,
        EngineDownloadState.Paused => DownloadState.Paused,
        EngineDownloadState.Complete => DownloadState.Completed,
        EngineDownloadState.Error => DownloadState.Failed,
        _ => null, // Removed: says nothing about what the user wants.
    };

    /// <summary>Changes the state if <see cref="DownloadStateMachine"/> allows it; otherwise logs and returns false.</summary>
    private bool TrySetState(DownloadItem item, DownloadState state)
    {
        if (item.State == state)
        {
            return false;
        }

        if (!DownloadStateMachine.CanTransition(item.State, state))
        {
            _logger.LogWarning("Ignoring a change of download {Id} from {From} to {To}", item.Id, item.State, state);
            return false;
        }

        item.State = state;
        return true;
    }

    private void OnEngineStateChanged(object? sender, EngineState state)
    {
        Raise(EngineStateChanged, state);
        if (state == EngineState.Running && _initialized && sender is IDownloadEngine engine)
        {
            // aria2 restarted (or was started by a retry): its downloads came back from the session file
            // as of its last save, so bring them in step again. Not on the caller's thread: it holds the
            // engine's own lock.
            _ = Task.Run(() => ReconcileInBackgroundAsync(engine));
        }
    }

    private async Task ReconcileInBackgroundAsync(IDownloadEngine engine)
    {
        try
        {
            await ReconcileAsync(engine, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reconciling engine {EngineId} failed", engine.Id);
        }
    }

    private void OnEngineDownloadEvent(object? sender, EngineDownloadEvent e)
    {
        if (sender is IDownloadEngine engine)
        {
            // Read the new state now instead of waiting for the next poll tick.
            _ = Task.Run(() => RefreshOneAsync(engine, e.Handle));
        }
    }

    private async Task RefreshOneAsync(IDownloadEngine engine, string handle)
    {
        try
        {
            var status = await engine.GetStatusAsync(handle, CancellationToken.None);
            if (status is null)
            {
                return;
            }

            await RunLockedAsync(async changes =>
            {
                var item = _items.Values.FirstOrDefault(i => i.EngineId == engine.Id && i.EngineHandle == handle);
                if (item is not null && ApplyStatus(item, status, out var stateChanged))
                {
                    changes.Update(item);
                    if (stateChanged)
                    {
                        await SaveAsync(item);
                    }
                    else
                    {
                        _unsaved.Add(item.Id);
                    }
                }

                return true;
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not refresh download {Handle} after an engine event", handle);
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Poll tick failed");
            }

            CancellationTokenSource intervalChanged;
            TimeSpan interval;
            lock (_intervalLock)
            {
                intervalChanged = _intervalChanged;
                interval = _pollInterval;
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, intervalChanged.Token);
            try
            {
                await Task.Delay(interval, _time, wait.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The interval changed: poll now, then wait with the new interval.
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SaveAsync(DownloadItem item)
    {
        try
        {
            // Not cancellable: a half-written change would leave the database behind the engine.
            await _repository.UpdateAsync(item, CancellationToken.None);
            _lastSaved[item.Id] = _time.GetUtcNow();
            _unsaved.Remove(item.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save download {Id}", item.Id);
            _unsaved.Add(item.Id);
        }
    }

    private async Task<bool> TryEngineAsync(Func<Task> call, string action, DownloadItem item)
    {
        try
        {
            await call();
            return true;
        }
        catch (Exception ex) when (ex is EngineOperationException or IOException or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not {Action} download {Id} in the engine", action, item.Id);
            return false;
        }
    }

    private bool IsPathTaken(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            return true;
        }

        // Another download may be about to write the same file, or a completed one may still name it
        // (its file was moved away). Sharing a path would let one row open or delete the other's file.
        return _items.Values.Any(i => IsSamePath(i, path));
    }

    private static bool IsSamePath(DownloadItem item, string path) =>
        string.Equals(
            Path.GetFullPath(Path.Combine(item.SaveFolder, item.FileName)),
            Path.GetFullPath(path),
            StringComparison.OrdinalIgnoreCase);

    private void DeleteFile(string path)
    {
        try
        {
            File.Delete(path); // No error when the file does not exist.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }

    private static DownloadRequest ToRequest(DownloadItem item) => new()
    {
        Uri = new Uri(item.Url),
        SuggestedFileName = item.FileName,
        Headers = HttpHeaders.Copy(item.Headers),
        Referrer = item.Referrer,
        UserAgent = item.UserAgent,
    };

    private static Dictionary<string, EngineDownloadStatus> ToDictionary(IEnumerable<EngineDownloadStatus> statuses)
    {
        var byHandle = new Dictionary<string, EngineDownloadStatus>();
        foreach (var status in statuses)
        {
            byHandle[status.Handle] = status;
        }

        return byHandle;
    }

    private IDownloadEngine? EngineOf(DownloadItem item) => _engines.FirstOrDefault(e => e.Id == item.EngineId);

    private List<DownloadItem> Find(IEnumerable<Guid> ids) =>
        ids.Distinct().Select(id => _items.GetValueOrDefault(id)).OfType<DownloadItem>().ToList();

    /// <summary>
    /// Runs <paramref name="work"/> under the lock and raises the events it collected before releasing
    /// it, so handlers see the operations in the order they happened. Handlers must return quickly and
    /// must not call back into the manager synchronously (the UI only posts to its own thread).
    /// </summary>
    private async Task<T> RunLockedAsync<T>(Func<Changes, Task<T>> work, CancellationToken ct)
    {
        var changes = new Changes();
        await _gate.WaitAsync(ct);
        try
        {
            return await work(changes);
        }
        finally
        {
            try
            {
                RaiseChanges(changes.TakeSnapshot());
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private void RaiseChanges(Changes.Snapshot snapshot)
    {
        foreach (var item in snapshot.Added)
        {
            Raise(ItemAdded, item);
        }

        foreach (var id in snapshot.Removed)
        {
            Raise(ItemRemoved, id);
        }

        if (snapshot.Updated.Count > 0)
        {
            Raise(ItemsUpdated, snapshot.Updated);
        }
    }

    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, args);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A download event handler failed");
        }
    }

    /// <summary>What one operation changed, collected for the events.</summary>
    private sealed class Changes
    {
        public List<DownloadItem> Added { get; } = [];

        public List<Guid> Removed { get; } = [];

        private Dictionary<Guid, DownloadItem> Updated { get; } = [];

        public void Update(DownloadItem item) => Updated[item.Id] = item;

        public Snapshot TakeSnapshot()
        {
            var added = Added.Select(i => i.Clone()).ToList();
            var addedIds = added.Select(i => i.Id).ToHashSet();
            var updated = Updated.Values
                .Where(i => !addedIds.Contains(i.Id) && !Removed.Contains(i.Id))
                .Select(i => i.Clone())
                .ToList();
            return new Snapshot(added, Removed.ToList(), updated);
        }

        public sealed record Snapshot(List<DownloadItem> Added, List<Guid> Removed, List<DownloadItem> Updated);
    }
}
