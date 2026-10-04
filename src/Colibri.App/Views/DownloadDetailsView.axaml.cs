using Avalonia.Controls;
using Avalonia.Threading;
using Colibri.App.Services;
using Colibri.App.ViewModels;

namespace Colibri.App.Views;

public partial class DownloadDetailsView : UserControl
{
    private readonly DispatcherTimer _timer;

    public bool CanPopOut
    {
        get => PopOutButton.IsVisible;
        set => PopOutButton.IsVisible = value;
    }
    public DownloadDetailsView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) => { _timer.Start(); Refresh(); };
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        DataContextChanged += (_, _) => Refresh();
    }

    private void Refresh()
    {
        if (IsEffectivelyVisible && DataContext is DownloadDetailsViewModel vm) _ = vm.RefreshAsync();
    }

    private void OnPopOut(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (CanPopOut && DataContext is DownloadDetailsViewModel vm)
            ShowWindow(vm, TopLevel.GetTopLevel(this) as Window);
    }

    public static Window ShowWindow(DownloadDetailsViewModel vm, Window? owner) =>
        DialogService.ShowDownloadDetails(vm, owner);
}
