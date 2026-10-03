using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Colibri.App.Resources;
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

        var owner = MainWindow;
        if (owner is { IsVisible: true } && owner.WindowState != WindowState.Minimized)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.Show(owner);
        }
        else
        {
            // Opened while Colibri is in the background (later: from a browser capture): show it on top of
            // the other windows once, so it does not open hidden behind the browser.
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Topmost = true;
            window.Opened += (_, _) =>
            {
                window.Activate();
                window.Topmost = false;
            };
            window.Show();
        }
    }

    public void ShowSettings()
    {
        var owner = MainWindow;
        if (owner is null)
        {
            return;
        }

        var close = new Button { Content = Strings.Close, IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        var window = new Window
        {
            Title = Strings.CommandSettings,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 20,
                Children =
                {
                    new TextBlock { Text = Strings.SettingsPlaceholderText, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    close,
                },
            },
        };
        close.Click += (_, _) => window.Close();
        _ = window.ShowDialog(owner);
    }
}
