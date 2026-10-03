using Avalonia.Headless.XUnit;

namespace Colibri.App.Tests;

public class MainWindowTests
{
    [AvaloniaFact]
    public void Main_window_can_be_shown_headless()
    {
        var window = new MainWindow();

        window.Show();

        Assert.True(window.IsVisible);
        window.Close();
    }
}
