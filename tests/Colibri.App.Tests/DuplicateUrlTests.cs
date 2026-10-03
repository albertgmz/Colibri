using Avalonia.Headless.XUnit;
using Colibri.Core.Models;
using Colibri.Core.Services;

namespace Colibri.App.Tests;

public class DuplicateUrlTests
{
    [AvaloniaFact]
    public async Task Browser_duplicate_resume_accepts_only_after_transfer_is_queued()
    {
        await using var ui = await UiHarness.StartAsync(UiHarness.Item("a.zip", DownloadState.Paused));
        using var capture = new CaptureSession(TimeSpan.FromMinutes(1));
        var add = ui.ViewModel.CreateAddUrl(LinkContext.Empty, "https://example.com/a.zip");
        add.AttachCapture(capture);
        add.CloseRequested += (_, _) => add.WindowClosed();
        await add.DownloadCommand.ExecuteAsync(null);
        await add.ResumeDuplicateCommand.ExecuteAsync(null);
        Assert.Equal("accepted", capture.State);
        Assert.Equal(DownloadState.Queued, Assert.Single(await ui.Manager.GetItemsAsync(TestContext.Current.CancellationToken)).State);
    }

    [AvaloniaFact]
    public async Task Browser_new_download_refused_by_engine_remains_pending()
    {
        await using var ui = await UiHarness.StartAsync();
        using var capture = new CaptureSession(TimeSpan.FromMinutes(1));
        var add = ui.ViewModel.CreateAddUrl(LinkContext.Empty, "https://example.com/refused.zip");
        add.AttachCapture(capture);
        ui.Engine.AddFailure = new IOException("Engine refused");
        var closed = false;
        add.CloseRequested += (_, _) => closed = true;
        await add.DownloadCommand.ExecuteAsync(null);
        Assert.Equal("pending", capture.State);
        Assert.False(closed);
        Assert.NotNull(add.ErrorText);
    }

    [AvaloniaFact]
    public async Task Browser_duplicate_resume_rejection_does_not_accept_or_close()
    {
        await using var ui = await UiHarness.StartAsync(UiHarness.Item("a.zip", DownloadState.Paused));
        using var capture = new CaptureSession(TimeSpan.FromMinutes(1));
        var add = ui.ViewModel.CreateAddUrl(LinkContext.Empty, "https://example.com/a.zip");
        add.AttachCapture(capture);
        var closed = false;
        add.CloseRequested += (_, _) => closed = true;
        await add.DownloadCommand.ExecuteAsync(null);
        var original = Assert.Single(await ui.Manager.GetItemsAsync(TestContext.Current.CancellationToken));
        await ui.Engine.RemoveAsync(original.EngineHandle!, TestContext.Current.CancellationToken);
        ui.Engine.AddFailure = new IOException("Engine unavailable");
        await add.ResumeDuplicateCommand.ExecuteAsync(null);
        Assert.Equal("pending", capture.State);
        Assert.False(closed);
        Assert.NotNull(add.ErrorText);
    }

    [AvaloniaFact]
    public async Task Browser_duplicate_fallback_before_resume_keeps_original_paused()
    {
        await using var ui = await UiHarness.StartAsync(UiHarness.Item("a.zip", DownloadState.Paused));
        using var capture = new CaptureSession(TimeSpan.FromMinutes(1));
        var add = ui.ViewModel.CreateAddUrl(LinkContext.Empty, "https://example.com/a.zip");
        add.AttachCapture(capture);
        await add.DownloadCommand.ExecuteAsync(null);
        add.BrowserFallbackCommand.Execute(null);
        await add.ResumeDuplicateCommand.ExecuteAsync(null);
        Assert.Equal("browser", capture.State);
        Assert.Equal(DownloadState.Paused, Assert.Single(await ui.Manager.GetItemsAsync(TestContext.Current.CancellationToken)).State);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_browser_rollback_remains_pending_for_manual_attention(bool canceled)
    {
        await using var ui = await UiHarness.StartAsync(UiHarness.Item("a.zip", DownloadState.Paused));
        using var capture = new CaptureSession(TimeSpan.FromMinutes(1));
        var add = ui.ViewModel.CreateAddUrl(LinkContext.Empty, "https://example.com/a.zip");
        add.AttachCapture(capture);
        await add.DownloadCommand.ExecuteAsync(null);
        ui.Engine.OnResume = _ => { if (canceled) capture.Reject("browser"); };
        ui.Engine.FailureAfterResuming = canceled ? new OperationCanceledException(capture.Token) : new IOException("Lost resume reply");
        ui.Engine.PauseFailure = new IOException("Cannot pause");
        ui.Engine.RemoveFailure = new IOException("Cannot remove");
        ui.Engine.StopFailure = new IOException("Cannot stop");
        try
        {
            await add.ResumeDuplicateCommand.ExecuteAsync(null);
            if (!canceled) add.BrowserFallbackCommand.Execute(null);
            Assert.Equal("pending", capture.State);
            Assert.NotNull(add.ErrorText);
            Assert.Equal(DownloadState.Queued, Assert.Single(await ui.Manager.GetItemsAsync(TestContext.Current.CancellationToken)).State);
        }
        finally { ui.Engine.StopFailure = null; }
    }

    [AvaloniaFact]
    public async Task Browser_cancellation_during_duplicate_resume_pauses_the_ambiguous_engine_add()
    {
        await using var ui = await UiHarness.StartAsync(UiHarness.Item("a.zip", DownloadState.Paused));
        using var capture = new CaptureSession(TimeSpan.FromMinutes(1));
        var add = ui.ViewModel.CreateAddUrl(LinkContext.Empty, "https://example.com/a.zip");
        add.AttachCapture(capture);
        await add.DownloadCommand.ExecuteAsync(null);
        var original = Assert.Single(await ui.Manager.GetItemsAsync(TestContext.Current.CancellationToken));
        await ui.Engine.RemoveAsync(original.EngineHandle!, TestContext.Current.CancellationToken);
        ui.Engine.OnAdd = _ =>
        {
            capture.Reject("browser");
            Assert.Equal("pending", capture.State);
        };
        ui.Engine.FailureAfterAdding = new OperationCanceledException(capture.Token);
        await add.ResumeDuplicateCommand.ExecuteAsync(null);
        Assert.Equal("browser", capture.State);
        Assert.Contains(original.EngineHandle!, ui.Engine.Pauses);
        Assert.Equal(Colibri.Core.Engine.EngineDownloadState.Paused,
            (await ui.Engine.GetStatusAsync(original.EngineHandle!, TestContext.Current.CancellationToken))!.State);
        Assert.Equal(DownloadState.Paused, Assert.Single(await ui.Manager.GetItemsAsync(TestContext.Current.CancellationToken)).State);
    }

    [AvaloniaFact]
    public async Task Duplicate_is_not_started_before_user_chooses_and_resume_keeps_one_row()
    {
        await using var ui = await UiHarness.StartAsync(UiHarness.Item("a.zip", DownloadState.Paused));
        var add = ui.ViewModel.CreateAddUrl(LinkContext.Empty, "https://example.com/a.zip");
        await add.DownloadCommand.ExecuteAsync(null);
        Assert.True(add.IsDuplicatePrompt);
        Assert.True(add.CanResumeDuplicate);
        Assert.Single(await ui.Manager.GetItemsAsync(TestContext.Current.CancellationToken));
        await add.ResumeDuplicateCommand.ExecuteAsync(null);
        // Resumed transfers remain queued until the next engine status poll.
        Assert.Equal(DownloadState.Queued, Assert.Single(await ui.Manager.GetItemsAsync(TestContext.Current.CancellationToken)).State);
    }

    [AvaloniaFact]
    public async Task Download_again_creates_a_distinct_reserved_file_without_replacing_original()
    {
        await using var ui = await UiHarness.StartAsync(UiHarness.Item("a.zip", DownloadState.Completed));
        var add = ui.ViewModel.CreateAddUrl(LinkContext.Empty, "https://example.com/a.zip");
        await add.DownloadCommand.ExecuteAsync(null);
        Assert.True(add.IsDuplicatePrompt);
        Assert.False(add.CanResumeDuplicate);
        await add.RedownloadDuplicateCommand.ExecuteAsync(null);
        var items = await ui.Manager.GetItemsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.State == DownloadState.Completed && i.FileName == "a.zip");
        // The category folder may differ from an imported v1 row, so check path uniqueness.
        Assert.Equal(2, items.Select(i => Path.Combine(i.SaveFolder, i.FileName)).Distinct().Count());
    }
}
