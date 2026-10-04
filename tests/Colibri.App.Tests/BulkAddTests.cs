using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Colibri.App.ViewModels;
using Colibri.Core.Ipc;
using Colibri.Core.Models;
using Colibri.Core.Services;

namespace Colibri.App.Tests;

public sealed class BulkAddTests
{
    [AvaloniaFact]
    public async Task Failed_bulk_transfer_keeps_offer_pending_and_pauses_prior_successes()
    {
        await using var ui = await UiHarness.StartAsync();
        using var capture = new CaptureSession(TimeSpan.FromMinutes(1));
        var view = new BulkAddViewModel(ui.Manager, [
            new AddRequest("https://example.com/first.zip", null, LinkContext.Empty),
            new AddRequest("https://example.com/second.zip", null, LinkContext.Empty)], capture);
        var calls = 0;
        ui.Engine.OnAdd = _ => { if (++calls == 2) ui.Engine.AddFailure = new IOException("Refused"); };
        await view.DownloadCommand.ExecuteAsync(null);
        Assert.Equal("pending", capture.State);
        Assert.NotNull(view.Error);
        var items = await ui.Manager.GetItemsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DownloadState.Paused, items.Single(item => item.Url.EndsWith("first.zip", StringComparison.Ordinal)).State);
        Assert.Equal(DownloadState.Failed, items.Single(item => item.Url.EndsWith("second.zip", StringComparison.Ordinal)).State);
    }

    [AvaloniaFact]
    public async Task Reassociation_relinquishes_offer_and_opens_only_selected_public_source_pages()
    {
        await using var ui = await UiHarness.StartAsync();
        using var capture = new CaptureSession(TimeSpan.FromMinutes(1));
        var publicLink = new AddRequest("https://files.test/stale?token=sensitive", null,
            new LinkContext { Referrer = "https://example.test/source", Cookies = "session=sensitive", FileName = "public.zip" });
        var privateLink = new AddRequest("https://private.test/stale", null,
            new LinkContext { Referrer = "https://private.test/page", FileName = "private.zip" }, PrivateWindow: true);
        var view = new BulkAddViewModel(ui.Manager, [publicLink, publicLink, privateLink], capture, ui.Shell);
        var closed = false;
        view.CloseRequested += (_, _) => closed = true;
        Assert.Equal(2, view.VisibleLinks.Count);
        Assert.DoesNotContain("sensitive", view.VisibleLinks[0].DisplayUrl);
        await view.ReassociateCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("browser", capture.State);
        Assert.Equal("https://example.test/source", Assert.Single(ui.Shell.OpenedUrls).AbsoluteUri);
        Assert.False(closed); // Keep instructions visible until the user closes them.
        Assert.NotNull(view.Error);
        await view.DownloadCommand.ExecuteAsync(null);
        Assert.Empty(ui.Engine.Adds);
        view.CancelCommand.Execute(null);
        Assert.True(closed);
    }
}
