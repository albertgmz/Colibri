using System.Text;
using Colibri.Core.Engine;
using Colibri.Core.Models;
using Colibri.Core.Network;
using Colibri.Core.Services;
using Colibri.Core.Settings;
using Colibri.Core.Tests.Fakes;
using Colibri.Core.Torrents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Colibri.Core.Tests.Services;

public sealed class TorrentManagerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static TorrentMetadataResult Preview() => TorrentMetainfo.Parse(Encoding.UTF8.GetBytes(
        "d4:infod5:filesld6:lengthi4e4:pathl5:a.bineed6:lengthi4e4:pathl5:b.bineee4:name6:bundle12:piece lengthi8e6:pieces20:12345678901234567890ee"));

    [Fact]
    public async Task MetadataIsExplicitAndProxyPolicyRejectsBeforePeerOperation()
    {
        using var paths = new TempAppPaths();
        var engine = new FakeEngine { MetadataPreview = Preview() };
        var settings = new AppSettings { DefaultNetworkPolicy = new() { Proxy = new() { Endpoint = new("http://localhost:8080") } } };
        var manager = Create(engine, new(), paths, new(), settings);
        await manager.InitializeAsync(Ct);
        await Assert.ThrowsAsync<EngineOperationException>(() => manager.FetchMagnetMetadataAsync(
            "magnet:?xt=urn:btih:" + Preview().Metadata.InfoHash, Ct));
        Assert.Equal(0, engine.MetadataPreviewCount);
        Assert.Empty(engine.Adds);
        Assert.Empty(await manager.GetItemsAsync(Ct));
        await manager.StopAsync();
    }

    [Fact]
    public async Task SelectionPersistsAndUpdatesRemainPausedInStoppedQueue()
    {
        using var paths = new TempAppPaths();
        var engine = new FakeEngine();
        var repository = new InMemoryDownloadRepository();
        var time = new ManualTimeProvider();
        var manager = Create(engine, repository, paths, time, new());
        await manager.InitializeAsync(Ct);
        var queue = await manager.CreateQueueAsync("Torrents", 1, Ct);
        var item = await manager.AddTorrentAsync(Preview(), [2], paths.DefaultDownloadsDirectory, queue.Id, new(), Ct);
        Assert.True(engine.Adds.Single().StartPaused);
        Assert.Equal(4, item.TotalBytes);
        await manager.UpdateTorrentAsync(item.Id, [1], new(Enabled: true), Ct);
        Assert.Equal(new[] { 1 }, repository.Stored(item.Id)!.Torrent!.SelectedFileIndices);
        Assert.Equal(DownloadState.Paused, repository.Stored(item.Id)!.State);
        Assert.True(engine.Adds.Last().StartPaused);
        await manager.StopAsync();
        var restartedEngine = new FakeEngine();
        var restarted = Create(restartedEngine, repository, paths, time, new());
        await restarted.InitializeAsync(Ct);
        Assert.Equal(new[] { 1 }, restartedEngine.Adds.Single().Request.Torrent!.SelectedFileIndices);
        Assert.True(restartedEngine.Adds.Single().StartPaused);
        await restarted.StopAsync();
    }

    [Fact]
    public async Task UploadCountsAndElapsedSeedBudgetSurviveRestart()
    {
        using var paths = new TempAppPaths();
        var time = new ManualTimeProvider();
        var repository = new InMemoryDownloadRepository();
        var engine = new FakeEngine();
        var manager = Create(engine, repository, paths, time, new());
        await manager.InitializeAsync(Ct);
        var item = await manager.AddTorrentAsync(Preview(), [1, 2], paths.DefaultDownloadsDirectory, Guid.Empty, new(true, 10, 10, 1024), Ct);
        engine.Report(item.EngineHandle!, EngineDownloadState.Active, total: 8, completed: 8, uploaded: 16, uploadSpeed: 1, isSeeding: true);
        await manager.PollOnceAsync(Ct);
        Assert.Equal(16, repository.Stored(item.Id)!.UploadedBytes);
        Assert.NotNull(repository.Stored(item.Id)!.SeedStartedAt);
        await manager.StopAsync();
        time.Advance(TimeSpan.FromMinutes(4));
        var restartedEngine = new FakeEngine();
        var restarted = Create(restartedEngine, repository, paths, time, new());
        await restarted.InitializeAsync(Ct);
        var seed = restartedEngine.Adds.Single().Request.Torrent!.SeedOptions;
        Assert.Equal(6, seed.TimeLimitMinutes);
        Assert.Equal(8, seed.Ratio);
        restartedEngine.Report(item.EngineHandle!, EngineDownloadState.Active, total: 8, completed: 8, uploaded: 2, isSeeding: true);
        await restarted.PollOnceAsync(Ct);
        Assert.Equal(18, (await restarted.GetItemsAsync(Ct)).Single().UploadedBytes);
        time.Advance(TimeSpan.FromMinutes(7));
        await restarted.PollOnceAsync(Ct);
        Assert.Equal(DownloadState.Completed, (await restarted.GetItemsAsync(Ct)).Single().State);
        await restarted.StopAsync();
    }

    private static DownloadManager Create(FakeEngine engine, InMemoryDownloadRepository repository, TempAppPaths paths,
        ManualTimeProvider time, AppSettings settings)
    {
        var manager = new DownloadManager([engine], repository, new([new DirectLinkResolver()]), settings, paths,
            NullLogger<DownloadManager>.Instance, time);
        manager.SetPollingInterval(Timeout.InfiniteTimeSpan);
        return manager;
    }

    [Fact]
    public async Task ChangedSelectionInvalidatesCompletionAndReconstructionCountsTheWholeNewUploadCounter()
    {
        using var paths = new TempAppPaths();
        var engine = new FakeEngine();
        var manager = Create(engine, new(), paths, new(), new());
        await manager.InitializeAsync(Ct);
        var item = await manager.AddTorrentAsync(Preview(), [1], paths.DefaultDownloadsDirectory, Guid.Empty, new(true, 10), Ct);
        engine.Report(item.EngineHandle!, EngineDownloadState.Active, total: 4, completed: 4, uploaded: 3, isSeeding: true);
        await manager.PollOnceAsync(Ct);
        await manager.UpdateTorrentAsync(item.Id, [2], new(true, 10), Ct);
        var changed = Assert.Single(await manager.GetItemsAsync(Ct));
        Assert.Equal(0, changed.CompletedBytes);
        Assert.Null(changed.SeedStartedAt);
        Assert.Equal(DownloadState.Paused, changed.State);
        await manager.ResumeAsync([item.Id], Ct);
        engine.Report(item.EngineHandle!, EngineDownloadState.Active, total: 4, completed: 1, uploaded: 5);
        await manager.PollOnceAsync(Ct);
        changed = Assert.Single(await manager.GetItemsAsync(Ct));
        Assert.Equal(8, changed.UploadedBytes);
        Assert.Equal(1, changed.CompletedBytes);
        Assert.NotEqual(DownloadState.Completed, changed.State);
        await manager.StopAsync();
    }
}
