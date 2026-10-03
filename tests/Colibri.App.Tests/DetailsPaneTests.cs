using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Colibri.App.Controls;
using Colibri.App.Resources;
using Colibri.Core.Engine;
using Colibri.Core.Models;
using Colibri.App.Views;
using Colibri.App.Services;
using Avalonia.Media;

namespace Colibri.App.Tests;

public class DetailsPaneTests
{
    [AvaloniaTheory]
    [InlineData(640)]
    [InlineData(960)]
    public async Task Compact_speed_and_log_content_never_overlaps_its_note(int width)
    {
        await using var ui = await UiHarness.StartAsync(UiHarness.Item("file.zip", DownloadState.Completed));
        ui.Settings.Layout.Width = width;
        ui.Settings.Layout.DetailsHeight = 180;
        var window = ui.ShowWindow();
        ui.ViewModel.SelectedItems = [ui.ViewModel.AllItems.Single()];
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(180, window.FindControl<Grid>("ContentGrid")!.RowDefinitions[2].Height.Value);
        var view = Pane(window).GetVisualDescendants().OfType<DownloadDetailsView>().Single();
        var tabs = view.FindControl<TabControl>("DetailTabs")!;
        tabs.SelectedIndex = 3;
        Dispatcher.UIThread.RunJobs();
        var graph = view.FindControl<SpeedGraph>("DetailsSpeedGraph")!;
        var speedNote = view.FindControl<TextBlock>("SpeedNote")!;
        var scroll = view.FindControl<ScrollViewer>("SpeedScroll")!;
        var graphTop = graph.TranslatePoint(default, scroll)!.Value.Y;
        var noteTop = speedNote.TranslatePoint(default, scroll)!.Value.Y;
        Assert.True(graph.Bounds.Height >= 70);
        Assert.True(graphTop + graph.Bounds.Height <= noteTop);
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        Dispatcher.UIThread.RunJobs();
        Assert.True(speedNote.TranslatePoint(default, scroll)!.Value.Y + speedNote.Bounds.Height <= scroll.Bounds.Height + 1);

        tabs.SelectedIndex = 4;
        Dispatcher.UIThread.RunJobs();
        var logNote = view.FindControl<TextBlock>("LogNote")!;
        var events = view.FindControl<ListBox>("DetailsEventList")!;
        Assert.True(logNote.TranslatePoint(default, tabs)!.Value.Y + logNote.Bounds.Height <= events.TranslatePoint(default, tabs)!.Value.Y);
    }

    [AvaloniaTheory]
    [InlineData(640)]
    [InlineData(960)]
    public async Task All_six_details_tabs_fit_a_single_compact_header_row(int width)
    {
        await using var ui = await UiHarness.StartAsync(UiHarness.Item("file.zip", DownloadState.Completed));
        ui.Settings.Layout.Width = width;
        var window = ui.ShowWindow();
        ui.ViewModel.SelectedItems = [ui.ViewModel.AllItems.Single()];
        Dispatcher.UIThread.RunJobs();
        var view = Pane(window).GetVisualDescendants().OfType<DownloadDetailsView>().Single();
        AssertCompactTabs(view);
        Assert.True(view.FindControl<Button>("PopOutButton")!.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Minimum_popout_uses_app_surfaces_platform_chrome_and_cannot_pop_out_again()
    {
        await using var ui = await UiHarness.StartAsync(UiHarness.Item("file.zip", DownloadState.Completed));
        ui.ViewModel.SelectedItems = [ui.ViewModel.AllItems.Single()];
        var chrome = new RecordingChrome();
        var popout = new DownloadDetailsWindow(chrome)
        {
            DataContext = ui.ViewModel.DetailViewModel, Width = 440, Height = 300,
        };
        try
        {
            Assert.Same(popout, chrome.AppliedWindow);
            popout.Show();
            Dispatcher.UIThread.RunJobs();
            var view = popout.FindControl<DownloadDetailsView>("DetailsContent")!;
            Assert.False(view.FindControl<Button>("PopOutButton")!.IsEffectivelyVisible);
            AssertCompactTabs(view);
            Assert.IsAssignableFrom<IBrush>(popout.Background);
            Assert.IsAssignableFrom<IBrush>(popout.FindControl<Border>("DetailsSurface")!.Background);
        }
        finally { popout.Close(); }
    }

    private static void AssertCompactTabs(DownloadDetailsView view)
    {
        var tabs = view.FindControl<TabControl>("DetailTabs")!;
        var headers = tabs.Items.OfType<TabItem>().ToArray();
        Assert.Equal(6, headers.Length);
        var first = headers[0].TranslatePoint(default, tabs)!.Value;
        foreach (var header in headers)
        {
            Assert.Equal(12, header.FontSize);
            Assert.Equal(12, Assert.IsType<TextBlock>(header.Header).FontSize);
            Assert.InRange(header.Bounds.Height, 24, 32);
            var position = header.TranslatePoint(default, tabs)!.Value;
            Assert.True(first.Y == position.Y,
                $"Header {((TextBlock)header.Header!).Text}: {header.Bounds}; position {position}; tabs width {tabs.Bounds.Width}.");
            Assert.True(position.X + header.Bounds.Width <= tabs.Bounds.Width + 1);
        }
    }

    private sealed class RecordingChrome : IWindowChrome
    {
        public Window? AppliedWindow { get; private set; }
        public void Apply(Window window) => AppliedWindow = window;
    }

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

        await ui.ViewModel.DetailViewModel!.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var texts = Texts(Pane(window));
        Assert.Contains("big.iso", texts);
        Assert.Contains("https://example.com/big.iso", texts);
        Assert.Contains(row.FilePath, texts);
        Assert.Contains(Strings.StateDownloading, texts);
        Assert.Contains("1.0 MB of 2.0 MB (50.0 %)", texts);
        Assert.Contains("100.0 KB/s", texts);
        Assert.Contains("8", texts);

        var detailsView = Pane(window).GetVisualDescendants().OfType<DownloadDetailsView>().Single();
        detailsView.FindControl<TabControl>("DetailTabs")!.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        var bar = detailsView.FindControl<SegmentBar>("SegmentBar")!;
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

    [AvaloniaFact]
    public async Task Tabs_and_resume_unknown_are_visible_and_popout_can_show_same_download()
    {
        await using var ui = await UiHarness.StartAsync(UiHarness.Item("file.zip", DownloadState.Completed));
        var window = ui.ShowWindow();
        ui.ViewModel.SelectedItems = [ui.ViewModel.AllItems.Single()];
        Dispatcher.UIThread.RunJobs();
        var texts = Texts(Pane(window));
        Assert.Contains(Strings.DetailsOverview, texts);
        Assert.Contains(Strings.DetailsPieces, texts);
        Assert.Contains(Strings.DetailsLog, texts);
        Assert.Contains(Strings.DetailsOptions, texts);
        Assert.Contains(Strings.DetailsResumeUnknown, texts);
        var popout = DownloadDetailsView.ShowWindow(ui.ViewModel.DetailViewModel!, window);
        Assert.Contains("file.zip", popout.Title);
        popout.Close();
    }

    [AvaloniaFact]
    public async Task Automatic_popout_is_off_by_default_and_only_fires_on_start_transition()
    {
        await using var ui = await UiHarness.StartAsync(UiHarness.Item("file.zip", DownloadState.Queued, handle: "a000000000000001"));
        var requests = 0;
        ui.ViewModel.DetailsWindowRequested += (_, _) => requests++;
        Assert.False(ui.Settings.AutoOpenDetailsWindow);
        ui.Settings.AutoOpenDetailsWindow = true;
        ui.Engine.Report("a000000000000001", EngineDownloadState.Active, total: 100, completed: 10, speed: 10);
        await ui.TickAsync();
        await ui.TickAsync();
        Assert.Equal(1, requests);
    }
}
