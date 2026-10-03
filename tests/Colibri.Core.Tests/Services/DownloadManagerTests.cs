using Colibri.Core.Engine;
using Colibri.Core.Models;
using Colibri.Core.Services;
using Colibri.Core.Settings;
using Colibri.Core.Tests.Fakes;
using Microsoft.Extensions.Logging;

namespace Colibri.Core.Tests.Services;

public sealed class DownloadManagerTests : IAsyncDisposable
{
    private readonly TempAppPaths _paths = new();
    private readonly FakeEngine _engine = new();
    private readonly ManualTimeProvider _time = new();
    private readonly ListLogger<DownloadManager> _logger = new();
    private readonly AppSettings _settings = new();
    private readonly List<DownloadManager> _managers = [];
    private InMemoryDownloadRepository _repository = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string CompressedFolder => Path.Combine(_paths.DefaultDownloadsDirectory, "Compressed");

    public async ValueTask DisposeAsync()
    {
        foreach (var manager in _managers)
        {
            await manager.StopAsync();
        }

        _paths.Dispose();
    }

    private DownloadManager Create(params DownloadItem[] seed)
    {
        _repository = new InMemoryDownloadRepository(seed);
        var manager = new DownloadManager(
            [_engine], _repository, new LinkResolverPipeline([new DirectLinkResolver()]), _settings, _paths, _logger, _time);

        // The tests drive polling themselves through PollOnceAsync.
        manager.SetPollingInterval(Timeout.InfiniteTimeSpan);
        _managers.Add(manager);
        return manager;
    }

    private async Task<DownloadManager> StartAsync(params DownloadItem[] seed)
    {
        var manager = Create(seed);
        await manager.InitializeAsync(Ct);
        return manager;
    }

    private static async Task<DownloadItem> AddAsync(DownloadManager manager, string url = "https://example.com/files/file.zip") =>
        (await manager.AddAsync(url, LinkContext.Empty, null, null, Ct)).Single();

    private static async Task<DownloadItem> ItemAsync(DownloadManager manager, Guid id) =>
        (await manager.GetItemsAsync(Ct)).Single(i => i.Id == id);

    private DownloadItem Seed(DownloadState state, string? handle, string name = "file.zip") => new()
    {
        Url = "https://example.com/" + name,
        FileName = name,
        SaveFolder = CompressedFolder,
        Category = DownloadCategory.Compressed,
        State = state,
        EngineId = "fake",
        EngineHandle = handle,
        TotalBytes = 1000,
        CompletedBytes = 400,
        CompletedAt = state == DownloadState.Completed ? _time.Now : null,
    };

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(10, Ct);
        }

        Assert.Fail("Condition was not met in time.");
    }

    // ---- Add ----

    [Fact]
    public async Task Add_sanitizes_the_name_picks_the_category_folder_persists_and_hands_it_to_the_engine()
    {
        var manager = await StartAsync();
        DownloadItem? added = null;
        manager.ItemAdded += (_, item) => added = item;

        var item = await AddAsync(manager, "https://example.com/files/my%3Areport%3F.zip");

        Assert.Equal("myreport.zip", item.FileName);
        Assert.Equal(DownloadCategory.Compressed, item.Category);
        Assert.Equal(CompressedFolder, item.SaveFolder);
        Assert.True(Directory.Exists(CompressedFolder));
        Assert.Equal(DownloadState.Queued, item.State);
        Assert.Equal("fake", item.EngineId);

        var call = Assert.Single(_engine.Adds);
        Assert.Equal(item.EngineHandle, call.Handle);
        Assert.Equal(CompressedFolder, call.SaveFolder);
        Assert.Equal("myreport.zip", call.FileName);
        Assert.False(call.StartPaused);

        var stored = _repository.Stored(item.Id)!;
        Assert.Equal(item.EngineHandle, stored.EngineHandle);
        Assert.Equal(_time.Now, stored.AddedAt);
        Assert.Equal(item.Id, added?.Id);
    }

    [Fact]
    public async Task Add_uses_the_category_folder_from_settings_and_the_overrides()
    {
        var custom = Path.Combine(_paths.DataDirectory, "MyVideos");
        _settings.CategoryFolders[DownloadCategory.Video] = custom;
        var manager = await StartAsync();

        var video = await AddAsync(manager, "https://example.com/clip.mp4");
        var other = Path.Combine(_paths.DataDirectory, "Elsewhere");
        var overridden = (await manager.AddAsync("https://example.com/clip.mp4", LinkContext.Empty, "renamed.pdf", other, Ct)).Single();

        Assert.Equal(custom, video.SaveFolder);
        Assert.Equal("renamed.pdf", overridden.FileName);
        Assert.Equal(DownloadCategory.Documents, overridden.Category);
        Assert.Equal(other, overridden.SaveFolder);
    }

    [Fact]
    public async Task Add_makes_the_name_unique_against_the_disk_and_unfinished_downloads()
    {
        Directory.CreateDirectory(CompressedFolder);
        await File.WriteAllTextAsync(Path.Combine(CompressedFolder, "file.zip"), "existing", Ct);
        var manager = await StartAsync();

        var first = await AddAsync(manager);
        var second = await AddAsync(manager);

        Assert.Equal("file (1).zip", first.FileName);
        Assert.Equal("file (2).zip", second.FileName);
    }

    [Fact]
    public async Task Add_keeps_cookies_with_the_headers_so_a_re_add_sends_them_again()
    {
        var manager = await StartAsync();
        var context = new LinkContext { Cookies = "session=1", Referrer = "https://example.com/page" };

        var item = (await manager.AddAsync("https://example.com/a.zip", context, null, null, Ct)).Single();

        Assert.Equal("session=1", item.Headers["cookie"]);
        Assert.Equal("https://example.com/page", item.Referrer);
    }

    [Fact]
    public async Task Add_rejects_an_invalid_url()
    {
        var manager = await StartAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => manager.AddAsync("javascript:alert(1)", LinkContext.Empty, null, null, Ct));
        Assert.Equal(0, _repository.Count);
    }

    [Fact]
    public async Task Engine_failure_on_add_keeps_the_item_as_failed_with_the_reason()
    {
        _engine.AddFailure = new EngineOperationException("aria2 said no");
        var manager = await StartAsync();

        var item = await AddAsync(manager);

        Assert.Equal(DownloadState.Failed, item.State);
        Assert.Equal("aria2 said no", item.ErrorMessage);
        Assert.Equal(DownloadState.Failed, _repository.Stored(item.Id)!.State);
        Assert.Single(await manager.GetItemsAsync(Ct));
    }

    [Fact]
    public async Task Add_while_the_engine_is_not_running_waits_queued_and_is_added_once_it_runs()
    {
        _engine.StateAfterStart = EngineState.NotFound;
        var manager = await StartAsync();

        var item = await AddAsync(manager);
        Assert.Equal(DownloadState.Queued, item.State);
        Assert.NotNull(item.EngineHandle); // Reserved up front.
        Assert.Empty(_engine.Adds);

        _engine.SetState(EngineState.Running);

        await WaitUntilAsync(() => Task.FromResult(_engine.Adds.Count > 0));
        Assert.Equal(item.EngineHandle, Assert.Single(_engine.Adds).Handle);
    }

    // ---- Pause, resume, delete ----

    [Fact]
    public async Task Pause_and_resume_go_through_the_engine_and_are_saved()
    {
        var manager = await StartAsync();
        var item = await AddAsync(manager);

        await manager.PauseAsync([item.Id], Ct);
        Assert.Equal(DownloadState.Paused, (await ItemAsync(manager, item.Id)).State);
        Assert.Equal(EngineDownloadState.Paused, _engine.Status(item.EngineHandle!)!.State);
        Assert.Equal(DownloadState.Paused, _repository.Stored(item.Id)!.State);

        await manager.ResumeAsync([item.Id], Ct);
        Assert.Equal(DownloadState.Queued, (await ItemAsync(manager, item.Id)).State);
        Assert.Equal(EngineDownloadState.Waiting, _engine.Status(item.EngineHandle!)!.State);
        Assert.Equal(DownloadState.Queued, _repository.Stored(item.Id)!.State);
    }

    [Fact]
    public async Task Pause_all_and_resume_all_affect_only_matching_downloads()
    {
        var manager = await StartAsync(Seed(DownloadState.Completed, "c000000000000001", "done.zip"));
        var a = await AddAsync(manager, "https://example.com/a.zip");
        var b = await AddAsync(manager, "https://example.com/b.zip");

        await manager.PauseAllAsync(Ct);
        var paused = await manager.GetItemsAsync(Ct);
        Assert.All(paused.Where(i => i.Id == a.Id || i.Id == b.Id), i => Assert.Equal(DownloadState.Paused, i.State));
        Assert.Contains(paused, i => i.State == DownloadState.Completed);

        await manager.ResumeAllAsync(Ct);
        Assert.Equal(2, (await manager.GetItemsAsync(Ct)).Count(i => i.State == DownloadState.Queued));
    }

    [Fact]
    public async Task Resume_of_a_failed_download_re_adds_it_with_the_same_handle_folder_and_name()
    {
        var manager = await StartAsync();
        var item = await AddAsync(manager);
        _engine.Report(item.EngineHandle!, EngineDownloadState.Error, error: "connection reset");
        await manager.PollOnceAsync(Ct);
        Assert.Equal(DownloadState.Failed, (await ItemAsync(manager, item.Id)).State);

        await manager.RetryAsync(item.Id, Ct);

        var retried = await ItemAsync(manager, item.Id);
        Assert.Equal(DownloadState.Queued, retried.State);
        Assert.Null(retried.ErrorMessage);
        Assert.Equal(2, _engine.Adds.Count);
        Assert.Equal(item.EngineHandle, _engine.Adds[1].Handle);
        Assert.Equal(item.SaveFolder, _engine.Adds[1].SaveFolder);
        Assert.Equal(item.FileName, _engine.Adds[1].FileName);
    }

    [Fact]
    public async Task Delete_with_files_removes_the_engine_download_the_files_and_the_row()
    {
        var manager = await StartAsync();
        var item = await AddAsync(manager);
        var path = Path.Combine(item.SaveFolder, item.FileName);
        await File.WriteAllTextAsync(path, "partial", Ct);
        await File.WriteAllTextAsync(path + ".aria2", "control", Ct);
        Guid? removed = null;
        manager.ItemRemoved += (_, id) => removed = id;

        await manager.DeleteAsync([item.Id], deleteFiles: true, Ct);

        Assert.Equal([item.EngineHandle!], _engine.Removes);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".aria2"));
        Assert.Null(_repository.Stored(item.Id));
        Assert.Empty(await manager.GetItemsAsync(Ct));
        Assert.Equal(item.Id, removed);
    }

    [Fact]
    public async Task A_completed_download_keeps_its_name_reserved_after_its_file_is_moved_away()
    {
        var manager = await StartAsync(Seed(DownloadState.Completed, null, "file.zip"));

        // The completed file is no longer on disk, but its row still points at it.
        var added = await AddAsync(manager);

        Assert.Equal("file (1).zip", added.FileName);
    }

    [Fact]
    public async Task Delete_with_files_never_deletes_a_file_another_row_points_at()
    {
        // Two rows sharing a path, as rows created before names were reserved could.
        var old = Seed(DownloadState.Completed, null, "shared.zip");
        var current = Seed(DownloadState.Paused, null, "shared.zip");
        Directory.CreateDirectory(CompressedFolder);
        var path = Path.Combine(CompressedFolder, "shared.zip");
        await File.WriteAllTextAsync(path, "in use", Ct);
        var manager = await StartAsync(old, current);

        await manager.DeleteAsync([old.Id], deleteFiles: true, Ct);

        Assert.True(File.Exists(path));
        Assert.Null(_repository.Stored(old.Id));
    }

    [Fact]
    public async Task Retry_while_the_engine_is_not_running_leaves_the_download_failed()
    {
        _engine.StateAfterStart = EngineState.NotFound;
        var failed = Seed(DownloadState.Failed, "a000000000000009");
        var manager = await StartAsync(failed);

        await manager.RetryAsync(failed.Id, Ct);

        Assert.Equal(DownloadState.Failed, (await ItemAsync(manager, failed.Id)).State);
        Assert.Empty(_engine.Adds);
    }

    [Fact]
    public async Task Add_rejects_a_relative_folder()
    {
        var manager = await StartAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.AddAsync("https://example.com/a.zip", LinkContext.Empty, null, "relative/folder", Ct));
        Assert.Equal(0, _repository.Count);
    }

    [Fact]
    public async Task A_poll_racing_a_resume_does_not_bring_back_the_old_paused_state()
    {
        var manager = await StartAsync();
        var item = await AddAsync(manager);
        await manager.PauseAsync([item.Id], Ct);

        // The user resumes right after the poll has read "paused" from the engine.
        Task? resume = null;
        _engine.AfterGetAll = () =>
        {
            _engine.AfterGetAll = null;
            resume = manager.ResumeAsync([item.Id], Ct);
        };
        await manager.PollOnceAsync(Ct);
        await resume!;

        Assert.Equal(DownloadState.Queued, (await ItemAsync(manager, item.Id)).State);
        Assert.Equal(DownloadState.Queued, _repository.Stored(item.Id)!.State);
    }

    [Fact]
    public async Task Delete_without_files_keeps_the_file()
    {
        var manager = await StartAsync();
        var item = await AddAsync(manager);
        var path = Path.Combine(item.SaveFolder, item.FileName);
        await File.WriteAllTextAsync(path, "data", Ct);

        await manager.DeleteAsync([item.Id], deleteFiles: false, Ct);

        Assert.True(File.Exists(path));
        Assert.Null(_repository.Stored(item.Id));
    }

    // ---- Handles ----

    [Fact]
    public async Task Add_stores_the_engine_handle_before_asking_the_engine()
    {
        var manager = await StartAsync();
        string? storedHandleDuringAdd = null;
        string? handleGiven = null;
        _engine.OnAdd = handle =>
        {
            handleGiven = handle;
            storedHandleDuringAdd = _repository.GetAllAsync(CancellationToken.None).Result.Single().EngineHandle;
        };

        var item = await AddAsync(manager);

        Assert.NotNull(handleGiven);
        Assert.Equal(handleGiven, storedHandleDuringAdd);
        Assert.Equal(handleGiven, item.EngineHandle);
    }

    [Fact]
    public async Task A_timed_out_add_that_reached_the_engine_is_found_again_by_its_handle()
    {
        var manager = await StartAsync();
        _engine.FailureAfterAdding = new TimeoutException("No answer from aria2.");

        var item = await AddAsync(manager);
        Assert.Equal(DownloadState.Failed, item.State);
        Assert.NotNull(item.EngineHandle);

        // aria2 did get it and is downloading.
        _engine.Report(item.EngineHandle!, EngineDownloadState.Active, total: 1000, completed: 100, speed: 10);
        await manager.ReconcileAsync(_engine, Ct);

        Assert.Empty(_engine.Removes); // Not mistaken for a stray download.
        var now = await ItemAsync(manager, item.Id);
        Assert.Equal(DownloadState.Active, now.State);
        Assert.Equal(100, now.CompletedBytes);
    }

    [Fact]
    public async Task Polling_copies_the_piece_bitfield_from_the_engine()
    {
        var seed = Seed(DownloadState.Active, "a000000000000009");
        _engine.Report("a000000000000009", EngineDownloadState.Active, total: 1000, completed: 500, bitfield: "f0", numPieces: 8);
        var manager = await StartAsync(seed);

        _engine.Report("a000000000000009", EngineDownloadState.Active, total: 1000, completed: 600, bitfield: "f8", numPieces: 8);
        await manager.PollOnceAsync(Ct);

        var item = await ItemAsync(manager, seed.Id);
        Assert.Equal("f8", item.Bitfield);
        Assert.Equal(8, item.NumPieces);
    }

    // ---- Reconcile ----

    [Fact]
    public async Task Reconcile_adopts_the_engine_state_of_a_known_download()
    {
        var seed = Seed(DownloadState.Queued, "a000000000000001");
        _engine.Report("a000000000000001", EngineDownloadState.Active, total: 1000, completed: 700, speed: 50);

        var manager = await StartAsync(seed);

        var item = await ItemAsync(manager, seed.Id);
        Assert.Equal(DownloadState.Active, item.State);
        Assert.Equal(700, item.CompletedBytes);
        Assert.Empty(_engine.Adds);
        Assert.Equal(DownloadState.Active, _repository.Stored(seed.Id)!.State);
    }

    [Fact]
    public async Task Reconcile_re_adds_an_unknown_download_with_the_same_handle_folder_and_name()
    {
        var seed = Seed(DownloadState.Active, "a000000000000002");

        var manager = await StartAsync(seed);

        var call = Assert.Single(_engine.Adds);
        Assert.Equal("a000000000000002", call.Handle);
        Assert.Equal(seed.SaveFolder, call.SaveFolder);
        Assert.Equal(seed.FileName, call.FileName);
        Assert.Equal(seed.Url, call.Request.Uri.AbsoluteUri);
        Assert.False(call.StartPaused);
        var item = await ItemAsync(manager, seed.Id);
        Assert.Equal(DownloadState.Queued, item.State);
        Assert.Equal(400, item.CompletedBytes); // Known progress is kept.
    }

    [Fact]
    public async Task Reconcile_keeps_the_paused_intent()
    {
        var unknown = Seed(DownloadState.Paused, "a000000000000003", "one.zip");
        var running = Seed(DownloadState.Paused, "a000000000000004", "two.zip");
        _engine.Report("a000000000000004", EngineDownloadState.Active, total: 1000, completed: 500, speed: 10);

        var manager = await StartAsync(unknown, running);

        var call = Assert.Single(_engine.Adds);
        Assert.Equal("a000000000000003", call.Handle);
        Assert.True(call.StartPaused);
        Assert.Equal(["a000000000000004"], _engine.Pauses);
        Assert.All(await manager.GetItemsAsync(Ct), i => Assert.Equal(DownloadState.Paused, i.State));
    }

    [Fact]
    public async Task Reconcile_resumes_a_download_paused_in_the_engine_but_wanted_running()
    {
        var seed = Seed(DownloadState.Active, "a000000000000005");
        _engine.Report("a000000000000005", EngineDownloadState.Paused);

        var manager = await StartAsync(seed);

        Assert.Equal(["a000000000000005"], _engine.Resumes);
        Assert.Equal(DownloadState.Queued, (await ItemAsync(manager, seed.Id)).State);
    }

    [Fact]
    public async Task Reconcile_leaves_completed_and_failed_downloads_alone()
    {
        var completed = Seed(DownloadState.Completed, "a000000000000006", "done.zip");
        var failed = Seed(DownloadState.Failed, "a000000000000007", "broken.zip");

        var manager = await StartAsync(completed, failed);

        Assert.Empty(_engine.Adds);
        Assert.Empty(_engine.Removes);
        var items = await manager.GetItemsAsync(Ct);
        Assert.Equal(DownloadState.Completed, items.Single(i => i.Id == completed.Id).State);
        Assert.Equal(DownloadState.Failed, items.Single(i => i.Id == failed.Id).State);
    }

    [Fact]
    public async Task Reconcile_removes_engine_downloads_that_are_not_in_the_database()
    {
        // Only the engine's remove is used, which never deletes files (see IDownloadEngine.RemoveAsync).
        var ours = Seed(DownloadState.Active, "a000000000000008", "ours.zip");
        _engine.Report("a000000000000008", EngineDownloadState.Active, total: 1000, completed: 500);
        _engine.Report("ffffffffffffffff", EngineDownloadState.Active);
        _engine.Report("eeeeeeeeeeeeeeee", EngineDownloadState.Complete);

        await StartAsync(ours);

        Assert.Equal(["eeeeeeeeeeeeeeee", "ffffffffffffffff"], _engine.Removes.Order());
        Assert.Null(_engine.Status("ffffffffffffffff"));
        Assert.NotNull(_engine.Status("a000000000000008"));
    }

    [Fact]
    public async Task A_failing_removal_of_a_leftover_does_not_stop_the_start()
    {
        _engine.Report("ffffffffffffffff", EngineDownloadState.Active);
        _engine.RemoveFailure = new InvalidOperationException("Unexpected aria2 error");

        var manager = await StartAsync();

        // Reconcile went on, and adding and polling still work.
        var item = await AddAsync(manager);
        _engine.Report(item.EngineHandle!, EngineDownloadState.Active, total: 1000, completed: 10);
        await manager.PollOnceAsync(Ct);
        Assert.Equal(DownloadState.Active, (await ItemAsync(manager, item.Id)).State);
    }

    [Fact]
    public async Task An_engine_restart_triggers_a_new_reconcile()
    {
        var manager = await StartAsync();
        var item = await AddAsync(manager);
        _engine.Forget(item.EngineHandle!); // aria2 lost it (e.g. crashed before saving its session).

        _engine.SetState(EngineState.Restarting);
        _engine.SetState(EngineState.Running);

        await WaitUntilAsync(() => Task.FromResult(_engine.Adds.Count == 2));
        Assert.Equal(item.EngineHandle, _engine.Adds[1].Handle);
    }

    // ---- Polling ----

    [Fact]
    public async Task Poll_updates_items_in_place_and_saves_progress_at_most_every_five_seconds()
    {
        var manager = await StartAsync();
        var item = await AddAsync(manager);
        var updates = new List<IReadOnlyList<DownloadItem>>();
        manager.ItemsUpdated += (_, batch) => updates.Add(batch);
        EngineGlobalStats? stats = null;
        manager.GlobalStatsChanged += (_, s) => stats = s;

        // First tick: Queued -> Active is a state change, saved at once.
        _engine.Report(item.EngineHandle!, EngineDownloadState.Active, total: 1000, completed: 100, speed: 500, connections: 4);
        await manager.PollOnceAsync(Ct);
        var savesAfterStateChange = _repository.UpdateCount;
        Assert.Equal(DownloadState.Active, _repository.Stored(item.Id)!.State);

        // Progress one second later: reported, but not written.
        _time.Advance(TimeSpan.FromSeconds(1));
        _engine.Report(item.EngineHandle!, EngineDownloadState.Active, total: 1000, completed: 300, speed: 500, connections: 4);
        await manager.PollOnceAsync(Ct);
        Assert.Equal(savesAfterStateChange, _repository.UpdateCount);
        Assert.Equal(100, _repository.Stored(item.Id)!.CompletedBytes);

        // Five seconds after the last save: written.
        _time.Advance(DownloadManager.ProgressSaveInterval);
        _engine.Report(item.EngineHandle!, EngineDownloadState.Active, total: 1000, completed: 600, speed: 500, connections: 4);
        await manager.PollOnceAsync(Ct);
        Assert.Equal(600, _repository.Stored(item.Id)!.CompletedBytes);

        var last = updates[^1].Single();
        Assert.Equal(600, last.CompletedBytes);
        Assert.Equal(500, last.DownloadSpeed);
        Assert.Equal(4, last.Connections);
        Assert.Equal(1000, last.TotalBytes);
        Assert.Equal(new EngineGlobalStats(500, 1, 0), stats);
    }

    [Fact]
    public async Task Poll_with_no_changes_raises_no_item_update()
    {
        var manager = await StartAsync();
        var item = await AddAsync(manager);
        _engine.Report(item.EngineHandle!, EngineDownloadState.Active, total: 1000, completed: 100, speed: 5);
        await manager.PollOnceAsync(Ct);
        var raised = 0;
        manager.ItemsUpdated += (_, _) => raised++;

        await manager.PollOnceAsync(Ct);

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Completion_is_saved_at_once_with_the_full_size()
    {
        var manager = await StartAsync();
        var item = await AddAsync(manager);

        _engine.Report(item.EngineHandle!, EngineDownloadState.Complete, total: 1000, completed: 1000);
        await manager.PollOnceAsync(Ct);

        var stored = _repository.Stored(item.Id)!;
        Assert.Equal(DownloadState.Completed, stored.State);
        Assert.Equal(1000, stored.CompletedBytes);
        Assert.Equal(_time.Now, stored.CompletedAt);
    }

    [Fact]
    public async Task Paused_downloads_restored_by_aria2_with_zero_sizes_keep_their_progress()
    {
        var seed = Seed(DownloadState.Paused, "a000000000000008");
        _engine.Report("a000000000000008", EngineDownloadState.Paused, total: 0, completed: 0);
        var manager = await StartAsync(seed);

        await manager.PollOnceAsync(Ct);

        var item = await ItemAsync(manager, seed.Id);
        Assert.Equal(1000, item.TotalBytes);
        Assert.Equal(400, item.CompletedBytes);
    }

    [Fact]
    public async Task An_illegal_engine_reported_transition_is_logged_and_ignored()
    {
        var manager = await StartAsync();
        var item = await AddAsync(manager);
        _engine.Report(item.EngineHandle!, EngineDownloadState.Error, error: "boom");
        await manager.PollOnceAsync(Ct);

        // Failed -> Paused is not allowed.
        _engine.Report(item.EngineHandle!, EngineDownloadState.Paused);
        await manager.PollOnceAsync(Ct);

        Assert.Equal(DownloadState.Failed, (await ItemAsync(manager, item.Id)).State);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("from Failed to Paused"));
    }

    [Fact]
    public async Task A_completed_download_ignores_later_engine_reports()
    {
        var manager = await StartAsync();
        var item = await AddAsync(manager);
        _engine.Report(item.EngineHandle!, EngineDownloadState.Complete, total: 1000, completed: 1000);
        await manager.PollOnceAsync(Ct);

        _engine.Report(item.EngineHandle!, EngineDownloadState.Active, total: 1000, completed: 10, speed: 99);
        await manager.PollOnceAsync(Ct);

        var current = await ItemAsync(manager, item.Id);
        Assert.Equal(DownloadState.Completed, current.State);
        Assert.Equal(1000, current.CompletedBytes);
    }

    [Fact]
    public async Task A_running_report_right_after_a_pause_does_not_undo_the_pause()
    {
        var manager = await StartAsync();
        var item = await AddAsync(manager);
        await manager.PauseAsync([item.Id], Ct);

        // aria2's forcePause has not taken effect yet.
        _engine.Report(item.EngineHandle!, EngineDownloadState.Active, total: 1000, completed: 200, speed: 10);
        await manager.PollOnceAsync(Ct);

        var current = await ItemAsync(manager, item.Id);
        Assert.Equal(DownloadState.Paused, current.State);
        Assert.Equal(0, current.DownloadSpeed);
    }

    [Fact]
    public async Task An_engine_download_event_refreshes_the_item_without_waiting_for_a_poll()
    {
        var manager = await StartAsync();
        var item = await AddAsync(manager);

        _engine.Report(item.EngineHandle!, EngineDownloadState.Complete, total: 1000, completed: 1000);
        _engine.RaiseDownloadEvent(item.EngineHandle!, EngineDownloadEventKind.Completed);

        await WaitUntilAsync(async () => (await ItemAsync(manager, item.Id)).State == DownloadState.Completed);
        Assert.Equal(DownloadState.Completed, _repository.Stored(item.Id)!.State);
    }

    [Fact]
    public async Task The_poll_loop_runs_on_its_own_once_initialized()
    {
        var manager = Create();
        manager.SetPollingInterval(TimeSpan.FromMilliseconds(20));
        var ticks = 0;
        manager.GlobalStatsChanged += (_, _) => Interlocked.Increment(ref ticks);

        await manager.InitializeAsync(Ct);

        await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref ticks) >= 2));
    }

    [Fact]
    public async Task Stop_saves_unsaved_progress_and_stops_the_engine()
    {
        var manager = await StartAsync();
        var item = await AddAsync(manager);
        _engine.Report(item.EngineHandle!, EngineDownloadState.Active, total: 1000, completed: 100);
        await manager.PollOnceAsync(Ct);
        _engine.Report(item.EngineHandle!, EngineDownloadState.Active, total: 1000, completed: 900);
        await manager.PollOnceAsync(Ct);
        Assert.Equal(100, _repository.Stored(item.Id)!.CompletedBytes);

        await manager.StopAsync();

        Assert.Equal(900, _repository.Stored(item.Id)!.CompletedBytes);
        Assert.Equal(1, _engine.StopCount);
    }
}
