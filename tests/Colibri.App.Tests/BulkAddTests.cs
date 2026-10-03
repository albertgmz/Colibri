using Avalonia.Headless.XUnit;
using Colibri.App.ViewModels;
using Colibri.Core.Ipc;
using Colibri.Core.Models;
using Colibri.Core.Services;

namespace Colibri.App.Tests;

public class BulkAddTests
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
}
