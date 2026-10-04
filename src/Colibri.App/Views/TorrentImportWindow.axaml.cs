using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Colibri.App.Resources;
using Colibri.App.ViewModels;

namespace Colibri.App.Views;

public partial class TorrentImportWindow : Window
{
    public TorrentImportWindow() => InitializeComponent();
    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TorrentImportViewModel viewModel) return;
        var start = Directory.Exists(viewModel.SaveFolder)
            ? await StorageProvider.TryGetFolderFromPathAsync(viewModel.SaveFolder) : null;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = Strings.TorrentDestination, AllowMultiple = false, SuggestedStartLocation = start });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) viewModel.SaveFolder = path;
    }
}
