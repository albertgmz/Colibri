using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Colibri.App.Resources;
using Colibri.App.ViewModels;
using Colibri.Core.Abstractions;
using Colibri.Core.Engine;
using Colibri.Core.Models;

namespace Colibri.App.Tests;

public class MainWindowTests
{
    private static List<string?> Texts(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

    private static DataGrid Grid(Window window) => window.FindControl<DataGrid>("DownloadsGrid")!;

    private static int RowCount(Window window) => Grid(window).GetVisualDescendants().OfType<DataGridRow>().Count(r => r.IsVisible);

    private static NavItemViewModel Nav(MainWindowViewModel viewModel, NavFilter filter) =>
        viewModel.NavItems.First(n => n.Filter == filter);

    [AvaloniaFact]
    public async Task Main_window_opens_and_shows_the_navigation_entries()
    {
        await using var ui = await UiHarness.StartAsync();

        var window = ui.ShowWindow();

        Assert.True(window.IsVisible);
        var texts = Texts(window);
        foreach (var label in new[] { "All", "Active", "Completed", "Failed", "Categories", "Compressed", "Documents", "Music", "Programs", "Video", "Other" })
        {
            Assert.Contains(label, texts);
        }

        Assert.Contains("Add URL", texts);
        Assert.Contains("aria2: running", texts);
    }

    [AvaloniaFact]
    public async Task Table_shows_one_row_per_download_with_formatted_values()
    {
        await using var ui = await UiHarness.StartAsync(
            UiHarness.Item("done.zip", DownloadState.Completed, total: 1_048_576, completed: 1_048_576),
            UiHarness.Item("half.mp4", DownloadState.Paused, total: 2_097_152, completed: 1_048_576),
            UiHarness.Item("broken.pdf", DownloadState.Failed, total: null));

        var window = ui.ShowWindow();

        Assert.Equal(3, RowCount(window));
        var texts = Texts(Grid(window));
        Assert.Contains("done.zip", texts);
        Assert.Contains("1.0 MB", texts);
        Assert.Contains("2.0 MB", texts);
        Assert.Contains("Unknown", texts);
        Assert.Contains("Completed", texts);
        Assert.Contains("50.0 % · Paused", texts);
        Assert.Contains("Failed", texts);
        Assert.Equal(3, Nav(ui.ViewModel, NavFilter.All).Count);
        Assert.Equal(1, Nav(ui.ViewModel, NavFilter.Completed).Count);
    }

    private static TextBlock EmptyHint(Window window) => window.FindControl<TextBlock>("EmptyHint")!;

    [AvaloniaFact]
    public async Task An_empty_list_shows_a_hint_until_the_first_download_is_added()
    {
        await using var ui = await UiHarness.StartAsync();
        var window = ui.ShowWindow();

        Assert.True(EmptyHint(window).IsEffectivelyVisible);
        Assert.Equal(Strings.EmptyListHint, EmptyHint(window).Text);

        var addUrl = ui.ViewModel.CreateAddUrl(LinkContext.Empty, null);
        addUrl.Url = "https://example.com/a.zip";
        await addUrl.DownloadCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(EmptyHint(window).IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task The_empty_list_hint_is_hidden_when_there_are_downloads()
    {
        await using var ui = await UiHarness.StartAsync(UiHarness.Item("done.zip", DownloadState.Completed));
        var window = ui.ShowWindow();

        Assert.False(EmptyHint(window).IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task The_empty_list_hint_gives_way_to_the_missing_engine_help()
    {
        await using var ui = await UiHarness.StartAsync(engine => engine.StateAfterStart = EngineState.NotFound);
        var window = ui.ShowWindow();

        Assert.True(ui.ViewModel.IsEngineMissing);
        Assert.False(EmptyHint(window).IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Add_url_creates_a_row_with_the_name_derived_from_the_url()
    {
        await using var ui = await UiHarness.StartAsync();
        var window = ui.ShowWindow();
        var addUrl = ui.ViewModel.CreateAddUrl(LinkContext.Empty, null);
        var closed = false;
        addUrl.CloseRequested += (_, _) => closed = true;

        addUrl.Url = "https://example.com/files/annual%20report.pdf";
        Assert.Equal("annual report.pdf", addUrl.FileName);
        Assert.EndsWith("Documents", addUrl.SaveFolder);
        Assert.Equal("Unknown", addUrl.SizeText);

        await addUrl.DownloadCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
        var row = Assert.Single(ui.ViewModel.AllItems);
        Assert.Equal("annual report.pdf", row.FileName);
        Assert.Equal(1, RowCount(window));
        Assert.Contains("annual report.pdf", Texts(Grid(window)));
        Assert.Single(ui.Engine.Adds);
    }

    [AvaloniaFact]
    public async Task Add_url_keeps_a_file_name_the_user_typed()
    {
        await using var ui = await UiHarness.StartAsync();
        var addUrl = ui.ViewModel.CreateAddUrl(LinkContext.Empty, "https://example.com/a.zip");

        addUrl.FileName = "mine.mp3";
        addUrl.Url = "https://example.com/b.zip";

        Assert.Equal("mine.mp3", addUrl.FileName);
        Assert.EndsWith("Music", addUrl.SaveFolder);
    }

    [AvaloniaFact]
    public async Task Add_url_shows_a_translated_error_for_an_invalid_url_and_adds_nothing()
    {
        await using var ui = await UiHarness.StartAsync();
        var addUrl = ui.ViewModel.CreateAddUrl(LinkContext.Empty, null);

        addUrl.Url = "javascript:alert(1)";
        await addUrl.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(Strings.UrlErrorUnsupportedScheme, addUrl.ErrorText);
        Assert.Empty(ui.ViewModel.AllItems);
    }

    [AvaloniaFact]
    public async Task Add_url_refuses_a_relative_folder_with_a_translated_message()
    {
        await using var ui = await UiHarness.StartAsync();
        var addUrl = ui.ViewModel.CreateAddUrl(LinkContext.Empty, "https://example.com/a.zip");

        addUrl.SaveFolder = "downloads";
        await addUrl.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(Strings.AddUrlFolderNotFullPath, addUrl.ErrorText);
        Assert.Empty(ui.ViewModel.AllItems);
    }

    [AvaloniaFact]
    public async Task Add_url_uses_a_browser_supplied_name_and_size()
    {
        await using var ui = await UiHarness.StartAsync();

        var addUrl = ui.ViewModel.CreateAddUrl(new LinkContext { FileName = "setup.exe", Size = 3_145_728 }, "https://example.com/download?id=1");

        Assert.Equal("setup.exe", addUrl.FileName);
        Assert.Equal("3.0 MB", addUrl.SizeText);
        Assert.EndsWith("Programs", addUrl.SaveFolder);
    }

    [AvaloniaFact]
    public async Task Add_url_lets_the_resolver_name_the_file_and_pick_its_folder_when_nothing_was_edited()
    {
        await using var ui = await UiHarness.StartWithResolverAsync(new NamingResolver("movie.mp4"));
        var addUrl = ui.ViewModel.CreateAddUrl(LinkContext.Empty, "https://example.com/download?id=1");
        Assert.Equal("download", addUrl.FileName);
        Assert.EndsWith("Other", addUrl.SaveFolder);

        await addUrl.DownloadCommand.ExecuteAsync(null);

        var item = Assert.Single(await ui.Manager.GetItemsAsync(CancellationToken.None));
        Assert.Equal("movie.mp4", item.FileName);
        Assert.Equal(ui.Manager.GetCategoryFolder(DownloadCategory.Video), item.SaveFolder);
    }

    [AvaloniaFact]
    public async Task Add_url_uses_a_file_name_the_user_edited_over_the_resolver_name()
    {
        await using var ui = await UiHarness.StartWithResolverAsync(new NamingResolver("movie.mp4"));
        var addUrl = ui.ViewModel.CreateAddUrl(LinkContext.Empty, "https://example.com/download?id=1");

        addUrl.FileName = "mine.zip";
        await addUrl.DownloadCommand.ExecuteAsync(null);

        var item = Assert.Single(await ui.Manager.GetItemsAsync(CancellationToken.None));
        Assert.Equal("mine.zip", item.FileName);
        Assert.Equal(ui.Manager.GetCategoryFolder(DownloadCategory.Compressed), item.SaveFolder);
    }

    [AvaloniaFact]
    public async Task Add_url_uses_a_folder_the_user_chose_and_still_the_resolver_name()
    {
        await using var ui = await UiHarness.StartWithResolverAsync(new NamingResolver("movie.mp4"));
        var addUrl = ui.ViewModel.CreateAddUrl(LinkContext.Empty, "https://example.com/download?id=1");
        var picked = Path.Combine(ui.Paths.DefaultDownloadsDirectory, "Picked");

        addUrl.SaveFolder = picked;
        await addUrl.DownloadCommand.ExecuteAsync(null);

        var item = Assert.Single(await ui.Manager.GetItemsAsync(CancellationToken.None));
        Assert.Equal("movie.mp4", item.FileName);
        Assert.Equal(picked, item.SaveFolder);
    }

    [AvaloniaFact]
    public async Task Add_url_keeps_a_browser_supplied_name_over_the_resolver_name()
    {
        await using var ui = await UiHarness.StartWithResolverAsync(new NamingResolver("movie.mp4"));
        var addUrl = ui.ViewModel.CreateAddUrl(new LinkContext { FileName = "setup.exe" }, "https://example.com/download?id=1");

        await addUrl.DownloadCommand.ExecuteAsync(null);

        var item = Assert.Single(await ui.Manager.GetItemsAsync(CancellationToken.None));
        Assert.Equal("setup.exe", item.FileName);
        Assert.Equal(ui.Manager.GetCategoryFolder(DownloadCategory.Programs), item.SaveFolder);
    }

    [AvaloniaFact]
    public async Task Add_url_counts_browsing_to_the_folder_already_shown_as_a_choice()
    {
        await using var ui = await UiHarness.StartWithResolverAsync(new NamingResolver("movie.mp4"));
        var addUrl = ui.ViewModel.CreateAddUrl(LinkContext.Empty, "https://example.com/download?id=1");
        var shown = addUrl.SaveFolder;

        addUrl.ChooseFolder(shown);
        await addUrl.DownloadCommand.ExecuteAsync(null);

        var item = Assert.Single(await ui.Manager.GetItemsAsync(CancellationToken.None));
        Assert.Equal("movie.mp4", item.FileName);
        Assert.Equal(shown, item.SaveFolder);
    }

    [AvaloniaFact]
    public async Task Add_url_hands_a_cleared_file_name_back_to_the_resolver()
    {
        await using var ui = await UiHarness.StartWithResolverAsync(new NamingResolver("movie.mp4"));
        var addUrl = ui.ViewModel.CreateAddUrl(new LinkContext { FileName = "setup.exe" }, "https://example.com/download?id=1");

        addUrl.FileName = "mine.zip";
        addUrl.FileName = string.Empty;
        await addUrl.DownloadCommand.ExecuteAsync(null);

        var item = Assert.Single(await ui.Manager.GetItemsAsync(CancellationToken.None));
        Assert.Equal("movie.mp4", item.FileName);
        Assert.Equal(ui.Manager.GetCategoryFolder(DownloadCategory.Video), item.SaveFolder);
    }

    [AvaloniaFact]
    public async Task Add_url_command_prefills_a_valid_url_from_the_clipboard_only()
    {
        await using var ui = await UiHarness.StartAsync();

        ui.Dialogs.ClipboardText = "  https://example.com/a.zip  ";
        await ui.ViewModel.AddUrlCommand.ExecuteAsync(null);
        Assert.Equal("https://example.com/a.zip", ui.Dialogs.ShownAddUrl!.Url);

        ui.Dialogs.ClipboardText = "just some text";
        await ui.ViewModel.AddUrlCommand.ExecuteAsync(null);
        Assert.Equal(string.Empty, ui.Dialogs.ShownAddUrl!.Url);
    }

    [AvaloniaFact]
    public async Task Completed_navigation_shows_only_completed_downloads()
    {
        await using var ui = await UiHarness.StartAsync(
            UiHarness.Item("done.zip", DownloadState.Completed),
            UiHarness.Item("paused.zip", DownloadState.Paused),
            UiHarness.Item("failed.zip", DownloadState.Failed));
        var window = ui.ShowWindow();

        ui.ViewModel.SelectedNav = Nav(ui.ViewModel, NavFilter.Completed);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, RowCount(window));
        Assert.Contains("done.zip", Texts(Grid(window)));
        Assert.DoesNotContain("paused.zip", Texts(Grid(window)));
    }

    [AvaloniaFact]
    public async Task Category_navigation_shows_only_that_category()
    {
        await using var ui = await UiHarness.StartAsync(
            UiHarness.Item("song.mp3", DownloadState.Completed),
            UiHarness.Item("movie.mkv", DownloadState.Completed));
        ui.ShowWindow();

        ui.ViewModel.SelectedNav = ui.ViewModel.NavItems.First(n => n.Category == DownloadCategory.Music);

        Assert.Equal(["song.mp3"], ui.ViewModel.Downloads.Cast<DownloadItemViewModel>().Select(r => r.FileName));
    }

    [AvaloniaFact]
    public async Task Search_filters_by_file_name_ignoring_case()
    {
        await using var ui = await UiHarness.StartAsync(
            UiHarness.Item("Annual-Report.pdf", DownloadState.Completed),
            UiHarness.Item("holiday.mp4", DownloadState.Completed),
            UiHarness.Item("report-2.pdf", DownloadState.Completed));
        var window = ui.ShowWindow();

        ui.ViewModel.SearchText = "REPORT";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, RowCount(window));
        Assert.DoesNotContain("holiday.mp4", Texts(Grid(window)));

        ui.ViewModel.SearchText = string.Empty;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, RowCount(window));
    }

    [AvaloniaFact]
    public async Task Progress_updates_the_existing_row_in_place_and_a_finished_row_leaves_the_active_view()
    {
        await using var ui = await UiHarness.StartAsync(
            UiHarness.Item("big.iso", DownloadState.Active, total: 1000, completed: 0, handle: "a000000000000001"));
        var window = ui.ShowWindow();
        ui.ViewModel.SelectedNav = Nav(ui.ViewModel, NavFilter.Active);
        var row = Assert.Single(ui.ViewModel.AllItems);

        ui.Engine.Report("a000000000000001", EngineDownloadState.Active, total: 1000, completed: 250, speed: 100);
        await ui.TickAsync();

        Assert.Same(row, Assert.Single(ui.ViewModel.AllItems));
        Assert.Equal(25, row.ProgressValue);
        Assert.Contains("25.0 %", Texts(Grid(window)));
        Assert.Contains("100 B/s", Texts(Grid(window)));
        Assert.Contains("8 s", Texts(Grid(window)));

        ui.Engine.Report("a000000000000001", EngineDownloadState.Complete, total: 1000, completed: 1000);
        await ui.TickAsync();

        Assert.Equal(0, RowCount(window));
        Assert.Equal(1, Nav(ui.ViewModel, NavFilter.Completed).Count);
    }

    [AvaloniaFact]
    public async Task Status_bar_shows_total_speed_and_active_count_and_zero_speed_when_nothing_is_active()
    {
        await using var ui = await UiHarness.StartAsync(
            UiHarness.Item("a.iso", DownloadState.Active, handle: "a000000000000003"),
            UiHarness.Item("b.iso", DownloadState.Paused, handle: "a000000000000004"));

        ui.Engine.Report("a000000000000003", EngineDownloadState.Active, total: 1000, completed: 10, speed: 2048);
        ui.Engine.Report("a000000000000004", EngineDownloadState.Paused, total: 1000, completed: 10);
        await ui.TickAsync();
        Assert.Equal("Speed: 2.0 KB/s", ui.ViewModel.SpeedText);
        Assert.Equal("Active: 1", ui.ViewModel.ActiveText);

        // aria2 keeps reporting a fading average speed after the last download stops.
        ui.Engine.Report("a000000000000003", EngineDownloadState.Paused, total: 1000, completed: 10, speed: 700);
        await ui.TickAsync();
        Assert.Equal("Speed: 0 B/s", ui.ViewModel.SpeedText);
        Assert.Equal("Active: 0", ui.ViewModel.ActiveText);
    }

    [AvaloniaFact]
    public async Task Commands_follow_the_selection()
    {
        await using var ui = await UiHarness.StartAsync(
            UiHarness.Item("paused.zip", DownloadState.Paused, handle: "a000000000000002"),
            UiHarness.Item("done.zip", DownloadState.Completed));
        var vm = ui.ViewModel;
        var paused = vm.AllItems.Single(r => r.FileName == "paused.zip");
        var done = vm.AllItems.Single(r => r.FileName == "done.zip");

        Assert.False(vm.DeleteCommand.CanExecute(null));

        vm.SelectedItems = [paused];
        Assert.True(vm.ResumeCommand.CanExecute(null));
        Assert.False(vm.PauseCommand.CanExecute(null));
        Assert.False(vm.OpenCommand.CanExecute(null));
        Assert.True(vm.DeleteCommand.CanExecute(null));

        vm.SelectedItems = [done];
        Assert.True(vm.OpenCommand.CanExecute(null));
        Assert.False(vm.ResumeCommand.CanExecute(null));
        await vm.OpenCommand.ExecuteAsync(null);
        Assert.Equal([done.FilePath], ui.Shell.Opened);

        vm.SelectedItems = [paused, done];
        Assert.False(vm.ShowInFolderCommand.CanExecute(null));
        Assert.True(vm.ResumeCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Selecting_rows_in_the_grid_reaches_the_view_model()
    {
        await using var ui = await UiHarness.StartAsync(UiHarness.Item("a.zip", DownloadState.Completed));
        var window = ui.ShowWindow();

        Grid(window).SelectedItem = ui.ViewModel.AllItems[0];
        Dispatcher.UIThread.RunJobs();

        Assert.Single(ui.ViewModel.SelectedItems);
        Assert.True(ui.ViewModel.CopyUrlCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Engine_not_found_shows_the_install_help_and_retry_starts_it()
    {
        await using var ui = await UiHarness.StartAsync(engine => engine.StateAfterStart = EngineState.NotFound);
        {
            var window = ui.ShowWindow();
            Assert.True(ui.ViewModel.IsEngineMissing);
            Assert.Contains(Strings.EngineMissingTitle, Texts(window));

            ui.Engine.StateAfterStart = EngineState.Running;
            await ui.ViewModel.RetryEngineCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.False(ui.ViewModel.IsEngineMissing);
            Assert.Equal(Strings.EngineStateRunning, ui.ViewModel.EngineStateText);
        }
    }

    [AvaloniaFact]
    public async Task A_thousand_rows_stay_fast_through_update_ticks()
    {
        var seed = Enumerable.Range(0, 1000)
            .Select(i => UiHarness.Item($"file-{i:D4}.zip", DownloadState.Active, total: 1_000_000, completed: 0, handle: $"{i:x16}"))
            .ToArray();
        await using var ui = await UiHarness.StartAsync(seed);
        var window = ui.ShowWindow();
        Assert.Equal(1000, ui.ViewModel.AllItems.Count);

        var watch = Stopwatch.StartNew();
        for (var tick = 1; tick <= 5; tick++)
        {
            for (var i = 0; i < 1000; i++)
            {
                ui.Engine.Report($"{i:x16}", EngineDownloadState.Active, total: 1_000_000, completed: tick * 1000 + i, speed: 1000 + tick);
            }

            await ui.TickAsync();
        }

        watch.Stop();

        // Generous bound; on a normal machine this takes well under a second.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"5 ticks over 1,000 rows took {watch.Elapsed}");
        Assert.Equal(5000, ui.ViewModel.AllItems.First(r => r.FileName == "file-0000.zip").CompletedBytes);

        // Virtualization: only the rows on screen exist as controls.
        Assert.True(RowCount(window) < 100, $"{RowCount(window)} row controls were created");
    }

    /// <summary>A resolver that handles every link and names the file <paramref name="fileName"/>.</summary>
    private sealed class NamingResolver(string fileName) : ILinkResolver
    {
        public string Id => "naming";

        public int Priority => 1;

        public Task<IReadOnlyList<DownloadRequest>?> ResolveAsync(Uri url, LinkContext context, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DownloadRequest>?>([new DownloadRequest { Uri = url, SuggestedFileName = fileName }]);
    }
}
