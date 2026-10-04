using Colibri.Core.Queues;
using Colibri.Core.Services;
using Colibri.Core.Models;
using Colibri.Core.Engine;
using Colibri.Core.Settings;
using Colibri.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Colibri.Core.Tests.Services;

public sealed class QueueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StoppedQueueCannotBeResumedByBulkRetryOrRestart()
    {
        using var paths = new TempAppPaths();
        var engine = new FakeEngine();
        var time = new ManualTimeProvider();
        var repository = new InMemoryDownloadRepository();
        var manager = Create(engine, repository, paths, time);
        await manager.InitializeAsync(Ct);
        var queue = await manager.CreateQueueAsync("Night", 1, Ct);
        var item = (await manager.AddAsync("https://example.com/a.zip", LinkContext.Empty, null, null, Ct, queue.Id)).Single();
        Assert.True(engine.Adds.Single().StartPaused);
        await manager.PauseAsync([item.Id], Ct);
        await manager.ResumeAllAsync(Ct);
        await manager.RetryAsync(item.Id, Ct);
        await manager.PollOnceAsync(Ct);
        Assert.Empty(engine.Resumes);
        Assert.True((await manager.GetItemsAsync(Ct)).Single().QueueHeld);
        await manager.StopAsync();

        var restartedEngine = new FakeEngine();
        var restarted = Create(restartedEngine, repository, paths, time);
        await restarted.InitializeAsync(Ct);
        Assert.True(restartedEngine.Adds.Single().StartPaused);
        Assert.False((await restarted.GetQueuesAsync(Ct)).Single(q => q.Id == queue.Id).IsRunning);
        await restarted.StopAsync();
    }

    [Fact]
    public async Task QueueConcurrencyAndExplicitPauseRemainIndependent()
    {
        using var paths = new TempAppPaths();
        var engine = new FakeEngine();
        var manager = Create(engine, new(), paths, new());
        await manager.InitializeAsync(Ct);
        var queue = await manager.CreateQueueAsync("Work", 1, Ct);
        await manager.SetQueueRunningAsync(queue.Id, true, Ct);
        var first = (await manager.AddAsync("https://example.com/a.zip", LinkContext.Empty, null, null, Ct, queue.Id)).Single();
        var second = (await manager.AddAsync("https://example.com/b.zip", LinkContext.Empty, null, null, Ct, queue.Id)).Single();
        Assert.False(engine.Adds[0].StartPaused);
        Assert.True(engine.Adds[1].StartPaused);
        await manager.PauseAsync([first.Id], Ct);
        await manager.PollOnceAsync(Ct);
        Assert.Contains(second.EngineHandle!, engine.Resumes);
        await manager.SetQueueRunningAsync(queue.Id, false, Ct);
        await manager.SetQueueRunningAsync(queue.Id, true, Ct);
        Assert.DoesNotContain(first.EngineHandle!, engine.Resumes);
        Assert.Equal(DownloadState.Paused, (await manager.GetItemsAsync(Ct)).Single(i => i.Id == first.Id).State);
        await manager.StopAsync();
    }

    [Fact]
    public async Task OneTimeScheduleUsesInjectedClockAndRecoversAfterDowntime()
    {
        using var paths = new TempAppPaths();
        var time = new ManualTimeProvider();
        var manager = Create(new(), new(), paths, time);
        await manager.InitializeAsync(Ct);
        var queue = await manager.CreateQueueAsync("Timed", 1, Ct);
        await manager.UpdateQueueAsync(queue with { Schedule = new() { StartAt = time.Now.AddMinutes(1), StopAt = time.Now.AddMinutes(2) } }, Ct);
        await manager.StopAsync();
        time.Advance(TimeSpan.FromMinutes(3));
        var recovered = Create(new(), new(), paths, time);
        await recovered.InitializeAsync(Ct);
        Assert.False((await recovered.GetQueuesAsync(Ct)).Single(q => q.Id == queue.Id).IsRunning);
        time.Advance(TimeSpan.FromMinutes(-2));
        await recovered.PollOnceAsync(Ct);
        Assert.False((await recovered.GetQueuesAsync(Ct)).Single(q => q.Id == queue.Id).IsRunning);
        await recovered.StopAsync();
    }

    [Fact]
    public async Task ScheduleStartsFromClockAndShutdownCancelsLongPollWait()
    {
        using var paths = new TempAppPaths();
        var time = new ManualTimeProvider();
        var manager = Create(new(), new(), paths, time);
        await manager.InitializeAsync(Ct);
        var queue = await manager.CreateQueueAsync("Clock", 1, Ct);
        await manager.UpdateQueueAsync(queue with { Schedule = new() { StartAt = time.Now.AddMinutes(1) } }, Ct);
        time.Advance(TimeSpan.FromMinutes(2));
        await manager.PollOnceAsync(Ct);
        Assert.True((await manager.GetQueuesAsync(Ct)).Single(q => q.Id == queue.Id).IsRunning);
        await manager.SetQueueRunningAsync(queue.Id, false, Ct);
        time.Advance(TimeSpan.FromMinutes(-2));
        await manager.PollOnceAsync(Ct);
        time.Advance(TimeSpan.FromMinutes(3));
        await manager.PollOnceAsync(Ct);
        Assert.False((await manager.GetQueuesAsync(Ct)).Single(q => q.Id == queue.Id).IsRunning);
        manager.SetPollingInterval(TimeSpan.FromDays(1));
        await manager.StopAsync().WaitAsync(TimeSpan.FromSeconds(3), Ct);
    }

    [Fact]
    public async Task EngineCapacityIsSumOfRunningQueueLimitsAndRetainsRateLimit()
    {
        using var paths = new TempAppPaths();
        var engine = new FakeEngine();
        var manager = Create(engine, new(), paths, new());
        await manager.InitializeAsync(Ct);
        var queue = await manager.CreateQueueAsync("Parallel", 2, Ct);
        await manager.SetQueueRunningAsync(queue.Id, true, Ct);
        Assert.Equal(5, engine.AppliedOptions!.MaxConcurrentDownloads);
        await manager.ApplyEngineOptionsAsync(new(3, 8, 12345), Ct);
        Assert.Equal(5, engine.AppliedOptions!.MaxConcurrentDownloads);
        Assert.Equal(12345, engine.AppliedOptions.GlobalSpeedLimitBytesPerSecond);
        Assert.Equal(8, engine.AppliedOptions.ConnectionsPerServer);
        await manager.SetQueueRunningAsync(queue.Id, false, Ct);
        Assert.Equal(3, engine.AppliedOptions!.MaxConcurrentDownloads);
        Assert.Equal(12345, engine.AppliedOptions.GlobalSpeedLimitBytesPerSecond);
        await manager.StopAsync();
    }

    [Fact]
    public async Task TwoEnginesShareCombinedSpeedBudgetWithoutZeroBecomingUnlimited()
    {
        using var paths = new TempAppPaths();
        var first = new FakeEngine();
        var second = new FakeEngine();
        var manager = new DownloadManager([first, second], new InMemoryDownloadRepository(),
            new([new DirectLinkResolver()]), new AppSettings(), paths, NullLogger<DownloadManager>.Instance, new ManualTimeProvider());
        await manager.ApplyEngineOptionsAsync(new(3, 8, 5), Ct);
        Assert.Equal(2, first.AppliedOptions!.GlobalSpeedLimitBytesPerSecond);
        Assert.Equal(2, second.AppliedOptions!.GlobalSpeedLimitBytesPerSecond);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => manager.ApplyEngineOptionsAsync(new(3, 8, 1), Ct));
        Assert.Equal(2, first.AppliedOptions!.GlobalSpeedLimitBytesPerSecond);
        await manager.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueueStopIsEnforcedBeforeEngineOptionsFailure(bool scheduled)
    {
        using var paths = new TempAppPaths();
        var engine = new FakeEngine();
        var time = new ManualTimeProvider();
        var manager = Create(engine, new(), paths, time);
        await manager.InitializeAsync(Ct);
        var queue = await manager.CreateQueueAsync("Stop failure", 1, Ct);
        await manager.SetQueueRunningAsync(queue.Id, true, Ct);
        var item = (await manager.AddAsync("https://example.com/a.zip", LinkContext.Empty, null, null, Ct, queue.Id)).Single();
        if (scheduled)
            await manager.UpdateQueueAsync(queue with { Schedule = new() { StopAt = time.Now.AddMinutes(1) } }, Ct);
        engine.ApplyOptionsFailure = new EngineOperationException("Options unavailable.");
        if (scheduled)
        {
            time.Advance(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<EngineOperationException>(() => manager.PollOnceAsync(Ct));
        }
        else
            await Assert.ThrowsAsync<EngineOperationException>(() => manager.SetQueueRunningAsync(queue.Id, false, Ct));
        Assert.False((await manager.GetQueuesAsync(Ct)).Single(q => q.Id == queue.Id).IsRunning);
        Assert.Equal(EngineDownloadState.Paused, engine.Status(item.EngineHandle!)!.State);
        Assert.True((await manager.GetItemsAsync(Ct)).Single().QueueHeld);
        await manager.StopAsync();
    }

    [Fact]
    public async Task HeldQueueCannotHideLiveTransferWhenEveryStopMethodFails()
    {
        using var paths = new TempAppPaths();
        var engine = new FakeEngine();
        var manager = Create(engine, new(), paths, new());
        await manager.InitializeAsync(Ct);
        var queue = await manager.CreateQueueAsync("Held", 1, Ct);
        var item = (await manager.AddAsync("https://example.com/a.zip", LinkContext.Empty, null, null, Ct, queue.Id)).Single();
        engine.Report(item.EngineHandle!, EngineDownloadState.Active, total: 1000, completed: 100, speed: 123, connections: 1);
        engine.IgnorePause = engine.IgnoreRemove = engine.IgnoreStop = true;
        await Assert.ThrowsAsync<DownloadCleanupException>(() => manager.PollOnceAsync(Ct));
        var current = (await manager.GetItemsAsync(Ct)).Single();
        Assert.True(current.QueueHeld);
        Assert.Equal(DownloadState.Active, current.State);
        Assert.Equal(123, current.DownloadSpeed);
        Assert.Equal(EngineDownloadState.Active, engine.Status(item.EngineHandle!)!.State);
        engine.IgnoreStop = false;
        await manager.StopAsync();
    }

    [Fact]
    public void WeeklyOvernightWindowStopsOnFollowingDay()
    {
        var schedule = new QueueSchedule { TimeZoneId = "UTC", Days = [DayOfWeek.Monday], StartTime = new(22, 0), StopTime = new(2, 0) };
        var monday = new DateTimeOffset(2026, 10, 5, 21, 0, 0, TimeSpan.Zero);
        Assert.True(QueueScheduleEvaluator.GetRunningState(schedule, monday, monday.AddHours(2)));
        Assert.False(QueueScheduleEvaluator.GetRunningState(schedule, monday, monday.AddHours(6)));
    }

    [Fact]
    public void DaylightSavingEdgesAreDeterministic()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero),
            QueueScheduleEvaluator.ToUtc(new(2026, 3, 8), new(2, 30), zone));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero),
            QueueScheduleEvaluator.ToUtc(new(2026, 11, 1), new(1, 30), zone));
        var schedule = new QueueSchedule { TimeZoneId = zone.Id, Days = [DayOfWeek.Sunday], StartTime = new(1, 30), StopTime = new(3, 0) };
        var firstOccurrence = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero);
        Assert.Null(QueueScheduleEvaluator.GetRunningState(schedule, firstOccurrence, firstOccurrence.AddHours(1)));
    }

    private static DownloadManager Create(FakeEngine engine, InMemoryDownloadRepository repository, TempAppPaths paths, ManualTimeProvider time)
    {
        var manager = new DownloadManager([engine], repository, new([new DirectLinkResolver()]), new AppSettings(), paths,
            NullLogger<DownloadManager>.Instance, time);
        manager.SetPollingInterval(Timeout.InfiniteTimeSpan);
        return manager;
    }
}
