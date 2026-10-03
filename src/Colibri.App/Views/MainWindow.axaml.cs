using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Colibri.App.ViewModels;

namespace Colibri.App.Views;

public partial class MainWindow : Window
{
    private const int DetailsRow = 2;
    private MainWindowViewModel? _subscribed;
    private GridLength _detailsHeight = new(180);

    public MainWindow()
    {
        InitializeComponent();
        InitializeLayout();
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

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_subscribed is not null)
        {
            _subscribed.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _subscribed = ViewModel;
        if (_subscribed is not null)
        {
            _subscribed.PropertyChanged += OnViewModelPropertyChanged;
            ApplyLayout(_subscribed.Layout);
            UpdateDetailsRow(_subscribed.IsDetailsVisible);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsDetailsVisible) or nameof(MainWindowViewModel.SelectedDetail)
            && ViewModel is { } viewModel)
        {
            UpdateDetailsRow(viewModel.IsDetailsVisible);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsDeleteConfirmationOpen) && ViewModel is { IsDeleteConfirmationOpen: true })
        {
            // Move the focus into the confirmation, so the keyboard does not keep working on the table behind it.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => DeleteCancelButton.Focus());
        }
    }

    // A row definition cannot be bound, and a hidden pane would still keep its row's height: the row is
    // set to zero while the pane is hidden, and gets back the height the user dragged it to.
    private void UpdateDetailsRow(bool visible)
    {
        var row = ContentGrid.RowDefinitions[DetailsRow];
        if (visible)
        {
            if (row.Height.Value > 24) _detailsHeight = row.Height;
            row.Height = ViewModel?.SelectedDetail is null ? new GridLength(24) : _detailsHeight;
            DetailsSplitter.IsVisible = ViewModel?.SelectedDetail is not null;
        }
        else
        {
            if (row.Height.Value > 24)
            {
                _detailsHeight = row.Height;
            }

            row.Height = new GridLength(0);
        }
    }

    // The DataGrid's SelectedItems cannot be bound, so the selection is handed to the view model here.
    // The row the user clicked last (the grid's SelectedItem) goes first: the details pane shows it.
    private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            var selected = DownloadsGrid.SelectedItems.OfType<DownloadItemViewModel>().ToList();
            if (DownloadsGrid.SelectedItem is DownloadItemViewModel current && selected.Remove(current))
            {
                selected.Insert(0, current);
            }

            viewModel.SelectedItems = selected;
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
