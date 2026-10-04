using Avalonia.Controls;
using Colibri.App.Services;

namespace Colibri.App.Views;

public partial class DownloadDetailsWindow : Window
{
    public DownloadDetailsWindow() : this(new DefaultWindowChrome())
    {
    }

    public DownloadDetailsWindow(IWindowChrome chrome)
    {
        InitializeComponent();
        chrome.Apply(this);
    }
}
