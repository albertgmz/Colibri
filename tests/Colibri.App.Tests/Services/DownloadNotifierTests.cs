using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Colibri.App.Services;
using Colibri.Core.Engine;
using Colibri.Core.Models;
using Colibri.Core.Platform;
using Microsoft.Extensions.Logging.Abstractions;

namespace Colibri.App.Tests.Services;

public class DownloadNotifierTests
{
    private static async Task<(UiHarness Ui, FakeNotifications Notifications)> StartAsync(params DownloadItem[] seed)
    {
        var (ui, notifications, notifier) = await StartWithoutActionsAsync(seed);
        notifier.StartHandlingActions(() => { });
        return (ui, notifications);
    }

    private static async Task<(UiHarness Ui, FakeNotifications Notifications, DownloadNotifier Notifier)> StartWithoutActionsAsync(params DownloadItem[] seed)
    {
        // The harness has already started the manager; a real app starts the notifier first, which makes
        // no difference here because stored downloads only become known through updates.
        var ui = await UiHarness.StartAsync(seed);
        var notifications = new FakeNotifications();
        var notifier = new DownloadNotifier(ui.Manager, notifications, ui.Shell, NullLogger<DownloadNotifier>.Instance);
        notifier.Start();
        return (ui, notifications, notifier);
    }

    [AvaloniaFact]
    public async Task A_download_that_completes_is_announced_once_with_its_path()
    {
        var (ui, notifications) = await StartAsync(UiHarness.Item("big.iso", DownloadState.Active, total: 100, handle: "a000000000000001"));
        await using var _ = ui;

        ui.Engine.Report("a000000000000001", EngineDownloadState.Active, total: 100, completed: 50, speed: 10);
        await ui.TickAsync();
        ui.Engine.Report("a000000000000001", EngineDownloadState.Complete, total: 100, completed: 100);
        await ui.TickAsync();
        await ui.TickAsync();
        Dispatcher.UIThread.RunJobs();

        var (item, path) = Assert.Single(notifications.Completed);
        Assert.Equal("big.iso", item.FileName);
        Assert.Equal(Path.Combine(item.SaveFolder, "big.iso"), path);
        Assert.Empty(notifications.Failed);
    }

    [AvaloniaFact]
    public async Task A_download_that_fails_is_announced_and_retry_goes_to_the_manager()
    {
        var (ui, notifications) = await StartAsync(UiHarness.Item("big.iso", DownloadState.Active, total: 100, handle: "a000000000000002"));
        await using var _ = ui;

        ui.Engine.Report("a000000000000002", EngineDownloadState.Active, total: 100, completed: 50);
        await ui.TickAsync();
        ui.Engine.Report("a000000000000002", EngineDownloadState.Error, error: "404");
        await ui.TickAsync();
        Dispatcher.UIThread.RunJobs();

        var failed = Assert.Single(notifications.Failed);
        notifications.Invoke(failed.Id, NotificationAction.Retry);

        await WaitUntilAsync(() => ui.ViewModel.AllItems.Single().State != DownloadState.Failed);
    }

    [AvaloniaFact]
    public async Task Downloads_finished_before_the_start_are_not_announced_and_actions_open_files()
    {
        var done = UiHarness.Item("done.zip", DownloadState.Completed, handle: "a000000000000003");
        var (ui, notifications) = await StartAsync(done);
        await using var _ = ui;
        ui.Engine.Report("a000000000000003", EngineDownloadState.Complete, total: 1_048_576, completed: 1_048_576);

        await ui.TickAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(notifications.Completed);

        // A download added this session that completes can be opened from its notification.
        var added = (await ui.Manager.AddAsync("https://example.com/new.zip", LinkContext.Empty, null, null, CancellationToken.None)).Single();
        ui.Engine.Report(added.EngineHandle!, EngineDownloadState.Complete, total: 10, completed: 10);
        await ui.TickAsync();
        Dispatcher.UIThread.RunJobs();

        var (item, path) = Assert.Single(notifications.Completed);
        notifications.Invoke(item.Id, NotificationAction.Open);
        notifications.Invoke(item.Id, NotificationAction.ShowInFolder);

        await WaitUntilAsync(() => ui.Shell.Opened.Count > 0 && ui.Shell.Revealed.Count > 0);
        Assert.Equal([path], ui.Shell.Opened);
        Assert.Equal([path], ui.Shell.Revealed);
    }

    [AvaloniaFact]
    public async Task Clicking_a_toast_of_a_download_from_an_earlier_session_opens_its_file()
    {
        // As after Windows started Colibri for a toast clicked once Colibri had exited.
        var done = UiHarness.Item("old.zip", DownloadState.Completed, handle: "a000000000000004");
        var (ui, notifications) = await StartAsync(done);
        await using var _ = ui;
        var path = Path.Combine(done.SaveFolder, "old.zip");

        notifications.Invoke(done.Id, NotificationAction.Open);
        notifications.Invoke(done.Id, NotificationAction.ShowInFolder);

        await WaitUntilAsync(() => ui.Shell.Opened.Count > 0 && ui.Shell.Revealed.Count > 0);
        Assert.Equal([path], ui.Shell.Opened);
        Assert.Equal([path], ui.Shell.Revealed);
    }

    [AvaloniaFact]
    public async Task Actions_that_arrive_before_the_app_is_ready_are_carried_out_once_it_is()
    {
        var done = UiHarness.Item("early.zip", DownloadState.Completed, handle: "a000000000000005");
        var (ui, notifications, notifier) = await StartWithoutActionsAsync(done);
        await using var _ = ui;
        var shown = 0;

        notifications.Invoke(done.Id, NotificationAction.Open);
        notifications.Invoke(done.Id, NotificationAction.Activate);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(ui.Shell.Opened);
        Assert.Equal(0, shown);

        notifier.StartHandlingActions(() => shown++);

        await WaitUntilAsync(() => ui.Shell.Opened.Count > 0 && shown > 0);
        Assert.Equal([Path.Combine(done.SaveFolder, "early.zip")], ui.Shell.Opened);
        Assert.Equal(1, shown);
    }

    [AvaloniaFact]
    public async Task Clicking_a_failed_download_notification_shows_the_main_window_on_the_ui_thread()
    {
        var (ui, notifications, notifier) = await StartWithoutActionsAsync();
        await using var _ = ui;
        var shownOnUiThread = new List<bool>();
        notifier.StartHandlingActions(() => shownOnUiThread.Add(Dispatcher.UIThread.CheckAccess()));

        // Raised from another thread, as Windows and D-Bus do.
        await Task.Run(() => notifications.Invoke(Guid.NewGuid(), NotificationAction.Activate), TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => shownOnUiThread.Count > 0);
        Assert.Equal([true], shownOnUiThread);
    }

    [AvaloniaFact]
    public async Task Actions_on_a_download_deleted_since_the_notification_do_nothing()
    {
        var (ui, notifications) = await StartAsync();
        await using var _ = ui;

        notifications.Invoke(Guid.NewGuid(), NotificationAction.Open);
        notifications.Invoke(Guid.NewGuid(), NotificationAction.ShowInFolder);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(ui.Shell.Opened);
        Assert.Empty(ui.Shell.Revealed);
    }

    [Fact]
    public void Notification_texts_come_from_the_app_resources()
    {
        var texts = AppServices.CreateNotificationTexts();

        Assert.Equal(Resources.Strings.NotificationCompleted, texts.DownloadCompleteTitle);
        Assert.Equal(Resources.Strings.NotificationFailed, texts.DownloadFailedTitle);
        Assert.Equal(Resources.Strings.NotificationOpen, texts.Open);
        Assert.Equal(Resources.Strings.NotificationShowInFolder, texts.ShowInFolder);
        Assert.Equal(Resources.Strings.NotificationRetry, texts.Retry);

        // "Download completed" is the app's wording; the platform's English default differs.
        Assert.NotEqual(new Colibri.Platform.Notifications.NotificationTexts().DownloadCompleteTitle, texts.DownloadCompleteTitle);
    }

    /// <summary>Waits for fire-and-forget work, letting the UI thread run meanwhile.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "Condition was not met in time.");
    }
}
