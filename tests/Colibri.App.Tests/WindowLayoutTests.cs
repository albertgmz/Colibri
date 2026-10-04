using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Colibri.App.Resources;
using Colibri.Core.Models;
using Colibri.Core.Settings;

namespace Colibri.App.Tests;

public class WindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(ToolbarMode.Labels, 960, 600)]
    [InlineData(ToolbarMode.Labels, 640, 400)]
    [InlineData(ToolbarMode.Icons, 960, 600)]
    [InlineData(ToolbarMode.Icons, 640, 400)]
    [InlineData(ToolbarMode.SmallIcons, 960, 600)]
    [InlineData(ToolbarMode.SmallIcons, 640, 400)]
    public async Task Layout_command_keeps_its_icon_name_and_tooltip_without_overlapping_search(
        ToolbarMode mode, int width, int height)
    {
        await using var ui = await UiHarness.StartAsync();
        ui.Settings.Layout.Toolbar = mode;
        ui.Settings.Layout.Width = width;
        ui.Settings.Layout.Height = height;
        var window = ui.ShowWindow();
        var layout = window.FindControl<Button>("LayoutButton")!;
        var search = window.FindControl<TextBox>("SearchBox")!;
        var commands = window.FindControl<StackPanel>("ToolbarCommands")!;
        var icon = layout.GetVisualDescendants().OfType<PathIcon>().Single();
        var label = layout.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == Strings.LayoutMenu);

        Assert.Contains("command", layout.Classes);
        Assert.Equal(Strings.LayoutMenu, AutomationProperties.GetName(layout));
        Assert.Equal(Strings.LayoutMenu, ToolTip.GetTip(layout));
        Assert.NotNull(icon.Data);
        Assert.Equal(mode == ToolbarMode.Labels, label.IsEffectivelyVisible);
        Assert.Equal(mode == ToolbarMode.SmallIcons ? 16 : 20, icon.Width);
        Assert.True(layout.Focus());
        Assert.True(layout.IsKeyboardFocusWithin);
        var searchOrigin = search.TranslatePoint(default, window)!.Value;
        var commandsOrigin = commands.TranslatePoint(default, window)!.Value;
        Assert.True(commandsOrigin.X + commands.Bounds.Width <= searchOrigin.X ||
                    commandsOrigin.Y + commands.Bounds.Height <= searchOrigin.Y);
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(layout.ContextMenu!.IsOpen);
        var choices = layout.ContextMenu.Items.OfType<MenuItem>().Select(item => item.Header).ToArray();
        Assert.Contains(Strings.LayoutSidebar, choices);
        Assert.Contains(Strings.LayoutCompact, choices);
        Assert.Contains(Strings.LayoutComfortable, choices);
        Assert.Contains(Strings.LayoutLabels, choices);
        Assert.Contains(Strings.LayoutIcons, choices);
        Assert.Contains(Strings.LayoutSmallIcons, choices);
        Assert.Contains(Strings.LayoutReset, choices);
        layout.ContextMenu.Close();
    }

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
    public async Task Magnet_paste_requires_opt_in_and_only_opens_a_preview()
    {
        await using var ui = await UiHarness.StartAsync();
        ui.Dialogs.ClipboardText = "magnet:?xt=urn:btih:0123456789012345678901234567890123456789&dn=fixture";
        await ui.ViewModel.PasteUrlAsync();
        Assert.Null(ui.Dialogs.ShownTorrent);
        ui.Settings.EnableMagnetClipboard = true;
        await ui.ViewModel.PasteUrlAsync();
        Assert.NotNull(ui.Dialogs.ShownTorrent);
        Assert.Null(ui.Dialogs.ShownAddUrl);
        Assert.Empty(ui.ViewModel.AllItems);
        Assert.Empty(await ui.Engine.GetAllAsync(CancellationToken.None));
        ui.Dialogs.ShownTorrent.WindowClosed();
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
