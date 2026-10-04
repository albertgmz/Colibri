using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Colibri.App.Resources;
using Colibri.App.ViewModels;
using Colibri.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Colibri.App.Services;

/// <summary><see cref="IDialogService"/> for the desktop app; the main window is the owner of dialogs.</summary>
public sealed class DialogService : IDialogService
{
    public static Window ShowDownloadDetails(DownloadDetailsViewModel viewModel, Window? owner)
    {
        // Resolve the OS-specific frame at the desktop UI boundary; headless tests/designers use the default.
        var window = Application.Current is App { Services: { } services }
            ? ActivatorUtilities.CreateInstance<DownloadDetailsWindow>(services)
            : new DownloadDetailsWindow();
        window.DataContext = viewModel;
        window.Title = $"{Strings.AppName} · {viewModel.Row.FileName}";
        window.WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        if (owner is null) window.Show(); else window.Show(owner);
        return window;
    }

    public void ShowBulkAdd(BulkAddViewModel viewModel)
    {
        var window = new BulkAddWindow { DataContext = viewModel, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        viewModel.CloseRequested += (_, _) => window.Close();
        window.Closed += (_, _) => viewModel.WindowClosed();
        window.Show();
        window.Activate();
    }
    public void ShowNetworkSettings(NetworkSettingsViewModel viewModel)
    {
        var window = new Window { Title = Strings.NetworkTitle, Width = 560, Height = 540,
            MinWidth = 400, MinHeight = 350,
            Content = new ScrollViewer { Content = new NetworkSettingsView { DataContext = viewModel } },
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        if (MainWindow is { } owner) window.Show(owner); else window.Show();
    }
    private static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public async Task<string?> PickTorrentFileAsync()
    {
        if (MainWindow is not { } owner) return null;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.CommandImportTorrent,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("BitTorrent") { Patterns = ["*.torrent"] }]
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public void ShowTorrentImport(TorrentImportViewModel viewModel)
    {
        var window = new TorrentImportWindow { DataContext = viewModel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        viewModel.CloseRequested += (_, _) => window.Close();
        window.Closed += (_, _) => viewModel.WindowClosed();
        if (MainWindow is { } owner) window.Show(owner); else window.Show();
    }

    public void ShowMedia(MediaViewModel viewModel)
    {
        var window = new MediaWindow { DataContext = viewModel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        viewModel.CloseRequested += (_, _) => window.Close();
        window.Closed += (_, _) => viewModel.WindowClosed();
        if (MainWindow is { } owner) window.Show(owner); else window.Show();
    }

    public async Task<string?> ReadClipboardTextAsync()
    {
        var clipboard = MainWindow?.Clipboard;
        return clipboard is null ? null : await clipboard.TryGetTextAsync();
    }

    public async Task WriteClipboardTextAsync(string text)
    {
        if (MainWindow?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    public void ShowAddUrl(AddUrlViewModel viewModel)
    {
        var window = new AddUrlWindow { DataContext = viewModel };
        viewModel.CloseRequested += (_, _) => window.Close();
        window.Closed += (_, _) => viewModel.WindowClosed();

        // A browser capture opens this window while another app has the focus, and the OS does not let a
        // background app take it. Being topmost until it has opened puts it in front of the browser anyway.
        window.Topmost = true;
        window.Opened += (_, _) =>
        {
            window.Activate();
            window.Topmost = false;
        };

        var owner = MainWindow;
        if (owner is { IsVisible: true } && owner.WindowState != WindowState.Minimized)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.Show(owner);
        }
        else
        {
            // The main window is hidden in the tray or minimized: show the window on its own.
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Show();
        }
    }
}
