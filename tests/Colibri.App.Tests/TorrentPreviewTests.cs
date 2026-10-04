using Avalonia.Headless.XUnit;
using Colibri.App.Resources;
using Colibri.App.ViewModels;

namespace Colibri.App.Tests;

public sealed class TorrentPreviewTests
{
    [AvaloniaFact]
    public async Task UnsupportedV2FileShowsV1LimitationWithoutStartingTransfer()
    {
        await using var ui = await UiHarness.StartAsync();
        var path = Path.Combine(ui.Paths.DataDirectory, "unsupported.torrent");
        Directory.CreateDirectory(ui.Paths.DataDirectory);
        await File.WriteAllTextAsync(path, "d4:infod12:meta versioni2eee", TestContext.Current.CancellationToken);
        var view = new TorrentImportViewModel(ui.Manager);
        await view.LoadSourceAsync(path);
        Assert.Equal(Strings.TorrentV1Only, view.Error);
        Assert.False(view.HasMetadata);
        Assert.Empty(ui.Engine.Adds);
        view.WindowClosed();
    }
}
