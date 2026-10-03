using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Colibri.Core.Models;
using Colibri.Core.Settings;

namespace Colibri.App.Tests;

public class WindowLayoutTests
{
    [AvaloniaFact]
    public async Task Saved_details_height_is_restored_after_a_new_window_is_created()
    {
        await using var ui = await UiHarness.StartAsync(UiHarness.Item("test.zip", DownloadState.Completed));
        ui.Settings.Layout.DetailsHeight = 240;
        var window = ui.ShowWindow();
        window.FindControl<DataGrid>("DownloadsGrid")!.SelectedItem = ui.ViewModel.AllItems[0];
        Assert.Equal(240, window.FindControl<Grid>("ContentGrid")!.RowDefinitions[2].Height.Value);
    }

    [AvaloniaFact]
    public async Task Pasted_URL_opens_add_but_invalid_text_does_not()
    {
        await using var ui = await UiHarness.StartAsync();
        ui.Dialogs.ClipboardText = "not a URL";
        await ui.ViewModel.PasteUrlAsync();
        Assert.Null(ui.Dialogs.ShownAddUrl);
        ui.Dialogs.ClipboardText = "  https://example.com/a.zip  ";
        await ui.ViewModel.PasteUrlAsync();
        Assert.Equal("https://example.com/a.zip", ui.Dialogs.ShownAddUrl!.Url);
        Assert.Empty(ui.ViewModel.AllItems);
    }

    [AvaloniaFact]
    public async Task Accent_choice_is_saved_and_loaded_without_changing_theme()
    {
        await using var ui = await UiHarness.StartAsync();
        await ui.SettingsPage.LoadAsync();
        ui.SettingsPage.AccentIndex = 1;
        await ui.SettingsPage.PendingWork;
        Assert.Equal("#0078D4", ui.SettingsStore.Saved!.AccentColor);
        Assert.Equal(AppTheme.System, ui.SettingsStore.Saved.Theme);
        await ui.SettingsPage.LoadAsync();
        Assert.Equal(1, ui.SettingsPage.AccentIndex);
    }

    [AvaloniaFact]
    public async Task Temporary_responsive_hiding_does_not_overwrite_user_visibility()
    {
        await using var ui = await UiHarness.StartAsync();
        var window = ui.ShowWindow();
        window.Width = 640;
        Dispatcher.UIThread.RunJobs();
        await window.PersistLayoutAsync();
        Assert.True(ui.SettingsStore.Saved!.Layout.Columns.Single(c => c.Key == "Speed").Visible);
        Assert.True(ui.SettingsStore.Saved.Layout.Columns.Single(c => c.Key == "AddedAt").Visible);
        Assert.False(ui.SettingsStore.Saved.Layout.Columns.Single(c => c.Key == "Host").Visible);
    }

    [AvaloniaFact]
    public async Task Dragged_details_height_is_kept_when_selection_is_cleared()
    {
        await using var ui = await UiHarness.StartAsync(UiHarness.Item("test.zip", DownloadState.Completed));
        var window = ui.ShowWindow();
        var table = window.FindControl<DataGrid>("DownloadsGrid")!;
        table.SelectedItem = ui.ViewModel.AllItems[0];
        window.FindControl<Grid>("ContentGrid")!.RowDefinitions[2].Height = new Avalonia.Controls.GridLength(240);
        ui.ViewModel.SelectedItems = [];
        await window.PersistLayoutAsync();
        Assert.Equal(240, ui.SettingsStore.Saved!.Layout.DetailsHeight);
    }

    [AvaloniaFact]
    public async Task No_selection_releases_details_space_and_selection_restores_it()
    {
        await using var ui = await UiHarness.StartAsync(UiHarness.Item("test.zip", DownloadState.Completed));
        var window = ui.ShowWindow();
        var content = window.FindControl<Grid>("ContentGrid")!;
        Assert.Equal(24, content.RowDefinitions[2].Height.Value);
        window.FindControl<DataGrid>("DownloadsGrid")!.SelectedItem = ui.ViewModel.AllItems[0];
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(180, content.RowDefinitions[2].Height.Value);
        ui.ViewModel.SelectedItems = [];
        Assert.Equal(24, content.RowDefinitions[2].Height.Value);
    }

    [AvaloniaFact]
    public async Task Layout_restores_geometry_and_user_column_preferences()
    {
        await using var ui = await UiHarness.StartAsync();
        ui.Settings.Layout = new WindowLayout
        {
            Width = 850, Height = 480, SidebarWidth = 190,
            Columns = [new() { Key = "Speed", Width = 150, Order = 0, Visible = false }],
        };
        var window = ui.ShowWindow();
        Assert.Equal(850, window.Width);
        Assert.Equal(480, window.Height);
        Assert.Equal(190, window.FindControl<Grid>("WorkspaceGrid")!.ColumnDefinitions[0].Width.Value);
        var speed = window.FindControl<DataGrid>("DownloadsGrid")!.Columns.Single(c => c.SortMemberPath == "Speed");
        Assert.False(speed.IsVisible);
        Assert.Equal(150, speed.Width.Value);
        Assert.Equal(0, speed.DisplayIndex);
    }
}
