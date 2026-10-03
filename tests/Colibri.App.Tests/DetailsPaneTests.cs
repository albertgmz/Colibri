using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Colibri.App.Controls;
using Colibri.App.Resources;
using Colibri.Core.Engine;
using Colibri.Core.Models;

namespace Colibri.App.Tests;

public class DetailsPaneTests
{
    private static Border Pane(Window window) => window.FindControl<Border>("DetailsPane")!;

    private static List<string?> Texts(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();

    [AvaloniaFact]
    public async Task Shows_the_selected_downloads_fields_and_its_segment_bar()
    {
        await using var ui = await UiHarness.StartAsync(
            UiHarness.Item("big.iso", DownloadState.Active, total: 2_097_152, completed: 0, handle: "a000000000000001"),
            UiHarness.Item("other.zip", DownloadState.Completed));
        var window = ui.ShowWindow();
        ui.Engine.Report("a000000000000001", EngineDownloadState.Active, total: 2_097_152, completed: 1_048_576, speed: 102_400,
            connections: 8, bitfield: "f0", numPieces: 8);
        await ui.TickAsync();

        Assert.Contains(Strings.DetailsEmpty, Texts(Pane(window)));

        var row = ui.ViewModel.AllItems.Single(r => r.FileName == "big.iso");
        window.FindControl<DataGrid>("DownloadsGrid")!.SelectedItem = row;
        Dispatcher.UIThread.RunJobs();

        var texts = Texts(Pane(window));
        Assert.Contains("big.iso", texts);
        Assert.Contains("https://example.com/big.iso", texts);
        Assert.Contains(row.FilePath, texts);
        Assert.Contains(Strings.StateDownloading, texts);
        Assert.Contains("1.0 MB of 2.0 MB (50.0 %)", texts);
        Assert.Contains("100.0 KB/s", texts);
        Assert.Contains("8", texts);

        var bar = window.FindControl<SegmentBar>("SegmentBar")!;
        Assert.Equal("f0", bar.Bitfield);
        Assert.Equal(8, bar.PieceCount);
        var filled = Assert.Single(bar.FilledRectangles(new Size(800, 14)));
        Assert.Equal(new Rect(0, 0, 400, 14), filled);
    }

    [AvaloniaFact]
    public async Task With_several_rows_selected_it_shows_the_first_one()
    {
        await using var ui = await UiHarness.StartAsync(
            UiHarness.Item("a.zip", DownloadState.Completed),
            UiHarness.Item("b.zip", DownloadState.Failed));
        var a = ui.ViewModel.AllItems.Single(r => r.FileName == "a.zip");
        var b = ui.ViewModel.AllItems.Single(r => r.FileName == "b.zip");

        ui.ViewModel.SelectedItems = [b, a];

        Assert.Same(b, ui.ViewModel.SelectedDetail);
        Assert.Equal(Strings.StateFailed, b.StatusText);
    }

    [AvaloniaFact]
    public async Task Hiding_the_pane_collapses_its_row_and_is_remembered()
    {
        await using var ui = await UiHarness.StartAsync();
        var window = ui.ShowWindow();
        var grid = window.FindControl<Grid>("ContentGrid")!;
        Assert.True(Pane(window).IsVisible);

        ui.ViewModel.IsDetailsVisible = false;
        Dispatcher.UIThread.RunJobs();

        Assert.False(Pane(window).IsVisible);
        Assert.Equal(0, grid.RowDefinitions[2].Height.Value);
        Assert.False(ui.SettingsStore.Saved!.ShowDetailsPane);

        ui.ViewModel.IsDetailsVisible = true;
        Assert.Equal(24, grid.RowDefinitions[2].Height.Value);
    }

    [AvaloniaFact]
    public async Task The_splitter_is_not_a_tab_stop()
    {
        await using var ui = await UiHarness.StartAsync();
        var window = ui.ShowWindow();

        // Focused by Tab it would only draw a focus frame across the window.
        Assert.False(window.FindControl<GridSplitter>("DetailsSplitter")!.IsTabStop);
    }
}
