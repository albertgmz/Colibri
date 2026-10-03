using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Colibri.App.Resources;
using Colibri.App.ViewModels;
using Colibri.Core.Models;
using Colibri.Core.Settings;

namespace Colibri.App.Tests;

public class SettingsPageTests
{
    [AvaloniaFact]
    public async Task Settings_command_swaps_the_table_for_the_settings_page_and_back()
    {
        await using var ui = await UiHarness.StartAsync();
        var window = ui.ShowWindow();
        var grid = window.FindControl<DataGrid>("DownloadsGrid")!;

        await ui.ViewModel.OpenSettingsCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(ui.ViewModel.IsSettingsOpen);
        Assert.False(grid.IsEffectivelyVisible);
        // Named controls of the page live in its own name scope, so search the visual tree.
        var closeToTray = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "CloseToTrayBox");
        Assert.True(closeToTray.IsEffectivelyVisible);

        ui.ViewModel.CloseSettingsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(grid.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Page_loads_the_current_values()
    {
        await using var ui = await UiHarness.StartAsync();
        ui.Settings.CloseToTray = false;
        ui.Settings.Theme = AppTheme.Dark;
        ui.Settings.ConnectionsPerServer = 4;
        ui.Settings.CategoryFolders = new() { [DownloadCategory.Music] = @"C:\Music" };
        ui.Settings.BrowserCaptureExtensions = ["zip", "iso"];
        ui.Autostart.Enabled = true;

        await ui.SettingsPage.LoadAsync();
        var page = ui.SettingsPage;

        Assert.False(page.CloseToTray);
        Assert.Equal(2, page.ThemeIndex);
        Assert.Equal(4, page.ConnectionsPerServer);
        Assert.Equal(@"C:\Music", page.CategoryFolders.Single(r => r.Category == DownloadCategory.Music).Folder);
        Assert.EndsWith("Video", page.CategoryFolders.Single(r => r.Category == DownloadCategory.Video).Placeholder);
        Assert.Equal("zip, iso", page.CaptureExtensions);
        Assert.True(page.StartWithSystem);
        Assert.Equal(0, ui.SettingsStore.SaveCount); // Loading saves nothing.
    }

    [AvaloniaFact]
    public async Task Changes_are_applied_and_saved_at_once()
    {
        await using var ui = await UiHarness.StartAsync();
        var page = ui.SettingsPage;
        await page.LoadAsync();
        var folder = Path.Combine(ui.Paths.DataDirectory, "Mine");

        page.CloseToTray = false;
        page.MinimizeToTray = true;
        page.ConnectionsPerServer = 8;
        page.SpeedLimitKiB = 500;
        page.MaxConcurrentDownloads = 99; // Clamped.
        page.DefaultFolder = folder;
        page.CategoryFolders.Single(r => r.Category == DownloadCategory.Video).Folder = Path.Combine(folder, "Films");
        page.CaptureExtensions = "ZIP, .rar;  7z zip bad!";
        page.CaptureMinSizeKiB = 1024;
        page.StartWithSystem = true;
        await page.PendingWork;

        var saved = ui.SettingsStore.Saved!;
        Assert.False(saved.CloseToTray);
        Assert.True(saved.MinimizeToTray);
        Assert.Equal(8, saved.ConnectionsPerServer);
        Assert.Equal(500, saved.GlobalSpeedLimitKiB);
        Assert.Equal(SettingsViewModel.MaxConcurrentLimit, saved.MaxConcurrentDownloads);
        Assert.Equal(folder, saved.DefaultDownloadFolder);
        Assert.Equal(Path.Combine(folder, "Films"), saved.CategoryFolders[DownloadCategory.Video]);
        Assert.Equal(["zip", "rar", "7z"], saved.BrowserCaptureExtensions);
        Assert.Equal("zip, rar, 7z", page.CaptureExtensions);
        Assert.Equal(1024, saved.BrowserCaptureMinSizeKiB);
        Assert.True(saved.StartWithSystem);
        Assert.True(ui.Autostart.Enabled);

        // The engine got the new options, and new downloads use the new folders.
        Assert.Equal(new Colibri.Core.Engine.EngineOptions(SettingsViewModel.MaxConcurrentLimit, 8, 500 * 1024L), ui.Engine.AppliedOptions);
        Assert.Equal(Path.Combine(folder, "Films"), ui.Manager.GetCategoryFolder(DownloadCategory.Video));
        Assert.Equal(Path.Combine(folder, "Music"), ui.Manager.GetCategoryFolder(DownloadCategory.Music));
    }

    [AvaloniaFact]
    public async Task A_relative_folder_is_refused_and_not_saved()
    {
        await using var ui = await UiHarness.StartAsync();
        var page = ui.SettingsPage;
        await page.LoadAsync();

        page.DefaultFolder = "downloads";

        Assert.Equal(Strings.AddUrlFolderNotFullPath, page.FolderError);
        Assert.Equal(string.Empty, ui.Settings.DefaultDownloadFolder);
        Assert.Null(ui.SettingsStore.Saved);
    }

    [AvaloniaFact]
    public async Task Use_default_clears_a_category_folder()
    {
        await using var ui = await UiHarness.StartAsync();
        ui.Settings.CategoryFolders = new() { [DownloadCategory.Music] = @"C:\Music" };
        await ui.SettingsPage.LoadAsync();
        var music = ui.SettingsPage.CategoryFolders.Single(r => r.Category == DownloadCategory.Music);

        music.UseDefaultCommand.Execute(null);
        await ui.SettingsPage.PendingWork;

        Assert.False(ui.SettingsStore.Saved!.CategoryFolders.ContainsKey(DownloadCategory.Music));
    }

    [AvaloniaFact]
    public async Task Theme_changes_live_and_is_saved()
    {
        await using var ui = await UiHarness.StartAsync();
        await ui.SettingsPage.LoadAsync();
        try
        {
            ui.SettingsPage.ThemeIndex = 2;
            await ui.SettingsPage.PendingWork;
            Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
            Assert.Equal(AppTheme.Dark, ui.SettingsStore.Saved!.Theme);

            ui.SettingsPage.ThemeIndex = 0;
            Assert.Equal(ThemeVariant.Default, Application.Current!.RequestedThemeVariant);
        }
        finally
        {
            // The headless Application is shared by all tests.
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        }
    }

    [AvaloniaFact]
    public async Task Changing_the_aria2_path_restarts_the_engine()
    {
        await using var ui = await UiHarness.StartAsync();
        await ui.SettingsPage.LoadAsync();
        var starts = ui.Engine.StartCount;

        ui.SettingsPage.Aria2Path = @"C:\tools\aria2c.exe";
        await ui.SettingsPage.PendingWork;

        Assert.Equal(@"C:\tools\aria2c.exe", ui.SettingsStore.Saved!.Aria2Path);
        Assert.Equal(starts + 1, ui.Engine.StartCount);
    }

    [AvaloniaFact]
    public async Task Without_autostart_support_or_a_tray_the_options_are_disabled_with_an_explanation()
    {
        await using var ui = await UiHarness.StartAsync();
        ui.Autostart.IsSupported = false;
        ui.ViewModel.SetTrayAvailable(false);

        Assert.False(ui.SettingsPage.IsAutostartSupported);
        Assert.False(ui.SettingsPage.IsTrayAvailable);
        Assert.False(ui.ViewModel.IsTrayAvailable);
    }

    [AvaloniaFact]
    public async Task Install_or_repair_registers_the_host_next_to_the_app_and_shows_each_browser()
    {
        await using var ui = await UiHarness.StartAsync();
        var window = ui.ShowWindow();
        await ui.ViewModel.OpenSettingsCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var page = ui.SettingsPage;

        Assert.True(page.IsBrowserStatusVisible);
        Assert.Equal(
            [new(Strings.BrowserChrome, Strings.BrowserStatusNotRegistered), new(Strings.BrowserEdge, Strings.BrowserStatusNotRegistered)],
            page.BrowserStatuses);
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "InstallBrowserButton");
        Assert.True(button.IsEffectivelyVisible);

        await page.InstallBrowserIntegrationCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var registered = ui.BrowserRegistrar.Registered!;
        Assert.Equal(AppContext.BaseDirectory, Path.GetDirectoryName(registered.HostExecutablePath) + Path.DirectorySeparatorChar);
        Assert.StartsWith("Colibri.NativeHost", Path.GetFileName(registered.HostExecutablePath));
        Assert.Equal([Colibri.Core.Platform.BrowserExtension.AllowedOrigin], registered.AllowedOrigins);
        Assert.All(page.BrowserStatuses, row => Assert.Equal(Strings.BrowserStatusRegistered, row.Status));
        Assert.Null(page.BrowserError);

        // The rows are on screen.
        var list = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "BrowserStatusList");
        Assert.Contains(list.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Strings.BrowserEdge);
    }

    [AvaloniaFact]
    public async Task A_failed_registration_shows_an_error_and_keeps_the_old_state()
    {
        await using var ui = await UiHarness.StartAsync();
        ui.BrowserRegistrar.FailWith = new UnauthorizedAccessException("denied");
        await ui.SettingsPage.LoadAsync();

        await ui.SettingsPage.InstallBrowserIntegrationCommand.ExecuteAsync(null);

        Assert.Equal(Strings.BrowserInstallFailed, ui.SettingsPage.BrowserError);
        Assert.All(ui.SettingsPage.BrowserStatuses, row => Assert.Equal(Strings.BrowserStatusNotRegistered, row.Status));
    }

    [AvaloniaFact]
    public async Task Open_logs_folder_opens_the_logs_directory()
    {
        await using var ui = await UiHarness.StartAsync();

        await ui.SettingsPage.OpenLogsFolderCommand.ExecuteAsync(null);

        Assert.Equal([ui.Paths.LogsDirectory], ui.Shell.OpenedFolders);
    }
}
