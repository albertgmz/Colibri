using Avalonia.Controls;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Colibri.App.Resources;
using Colibri.App.ViewModels;

namespace Colibri.App.Views;

/// <summary>The settings page. The pickers are the OS's own dialogs, opened through the window's storage provider.</summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateNavigation();
    }

    public event EventHandler<RoutedEventArgs>? LayoutRequested;

    private void OnLayoutClick(object? sender, RoutedEventArgs e) => LayoutRequested?.Invoke(sender, e);

    private void UpdateNavigation()
    {
        var compact = Bounds.Width < 760;
        PageNavigation.IsVisible = !compact;
        PageSelector.IsVisible = compact;
        SettingsGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 0 : 170);
    }

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    private async void OnBrowseDefaultFolder(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel && await PickFolderAsync(viewModel.DefaultFolder) is { } folder)
        {
            viewModel.DefaultFolder = folder;
        }
    }

    private async void OnBrowseCategoryFolder(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CategoryFolderViewModel row } && await PickFolderAsync(row.Folder) is { } folder)
        {
            row.Folder = folder;
        }
    }

    private async void OnBrowseAria2(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.SettingsAria2PickTitle,
            AllowMultiple = false,
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            viewModel.Aria2Path = path;
        }
    }

    /// <summary>Returns the chosen folder's path, or null when cancelled.</summary>
    private async Task<string?> PickFolderAsync(string current)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return null;
        }

        var start = Directory.Exists(current) ? await storage.TryGetFolderFromPathAsync(current) : null;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.AddUrlPickFolderTitle,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
