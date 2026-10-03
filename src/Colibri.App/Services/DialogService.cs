using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Colibri.App.ViewModels;
using Colibri.App.Views;

namespace Colibri.App.Services;

/// <summary><see cref="IDialogService"/> for the desktop app; the main window is the owner of dialogs.</summary>
public sealed class DialogService : IDialogService
{
    private static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

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
