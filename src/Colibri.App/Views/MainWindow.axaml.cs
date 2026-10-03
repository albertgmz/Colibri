using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Colibri.App.ViewModels;

namespace Colibri.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Poll aria2 less often while the window cannot be seen.
        if (change.Property == WindowStateProperty || change.Property == IsVisibleProperty)
        {
            ViewModel?.SetWindowVisible(IsVisible && WindowState != WindowState.Minimized);
        }
    }

    // The DataGrid's SelectedItems cannot be bound, so the selection is handed to the view model here.
    private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.SelectedItems = DownloadsGrid.SelectedItems.OfType<DownloadItemViewModel>().ToList();
        }
    }

    private void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        // The tapped element inherits the row's DataContext; the column header's is the window's.
        if (e.Source is StyledElement { DataContext: DownloadItemViewModel item } && ViewModel is { } viewModel)
        {
            _ = viewModel.OpenItemAsync(item);
        }
    }
}
