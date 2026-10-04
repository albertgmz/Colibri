using Colibri.App.Resources;
using CommunityToolkit.Mvvm.Input;

namespace Colibri.App.ViewModels;

public partial class MainWindowViewModel
{
    [RelayCommand]
    private async Task MediaAsync()
    {
        if (_mediaHelper is null) return;
        var view = new MediaViewModel(_manager, _mediaHelper, _settings);
        _dialogs.ShowMedia(view);
        await view.LoadAsync(CancellationToken.None);
    }

    private async Task ShowTorrentSourceAsync(string source)
    {
        var view = new TorrentImportViewModel(_manager);
        // Loading a magnet only validates and displays it. Metadata requires an explicit command.
        _dialogs.ShowTorrentImport(view);
        await view.LoadSourceAsync(source);
    }

    [RelayCommand]
    private Task ImportTorrentAsync() => RunSafeAsync(async () =>
    {
        if (await _dialogs.PickTorrentFileAsync() is { } path) await ShowTorrentSourceAsync(path);
    }, "import a torrent");

    [RelayCommand]
    private Task EditTorrentAsync() => RunSafeAsync(async () =>
    {
        if (_selectedItems.Count != 1) return;
        var item = (await _manager.GetItemsAsync(CancellationToken.None)).FirstOrDefault(i => i.Id == _selectedItems[0].Id);
        if (item?.Torrent is null) return;
        var view = new TorrentImportViewModel(_manager);
        _dialogs.ShowTorrentImport(view);
        await view.LoadDownloadAsync(item);
    }, "edit torrent files and seeding");

    [RelayCommand]
    private Task StopSeedingAsync() => RunSafeAsync(async () =>
    {
        foreach (var id in SelectedIds()) await _manager.StopSeedingAsync(id, CancellationToken.None);
    }, "stop seeding");
}
