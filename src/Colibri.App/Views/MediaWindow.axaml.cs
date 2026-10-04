using Avalonia.Controls;
using Colibri.App.ViewModels;
namespace Colibri.App.Views;
public partial class MediaWindow : Window
{
    public MediaWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { if (DataContext is MediaViewModel vm) vm.CloseRequested += (_, _) => Close(); };
        Closing += (_, _) => (DataContext as MediaViewModel)?.CancelPending();
    }
}
