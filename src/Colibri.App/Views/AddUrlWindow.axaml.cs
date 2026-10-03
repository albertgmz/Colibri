using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Colibri.App.Resources;
using Colibri.App.ViewModels;

namespace Colibri.App.Views;

public partial class AddUrlWindow : Window
{
    public AddUrlWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UrlBox.Focus();
        UrlBox.SelectAll();
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddUrlViewModel viewModel)
        {
            return;
        }

        // The folder picker is the OS's own dialog; it returns an empty list when cancelled.
        var start = Directory.Exists(viewModel.SaveFolder)
            ? await StorageProvider.TryGetFolderFromPathAsync(viewModel.SaveFolder)
            : null;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.AddUrlPickFolderTitle,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            viewModel.ChooseFolder(path);
        }
    }
}
