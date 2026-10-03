using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Colibri.App.Tests;

public class FontMeasurementTests
{
    [AvaloniaFact]
    public void Layout_tests_measure_the_apps_proportional_font()
    {
        var narrow = new TextBlock { Text = "iiii", FontSize = 12 };
        var wide = new TextBlock { Text = "WWWW", FontSize = 12 };
        var available = new Size(double.PositiveInfinity, double.PositiveInfinity);
        narrow.Measure(available);
        wide.Measure(available);

        Assert.True(narrow.DesiredSize.Width > 0);
        Assert.True(wide.DesiredSize.Width > narrow.DesiredSize.Width * 2,
            $"Proportional font measurement expected; narrow={narrow.DesiredSize.Width}, wide={wide.DesiredSize.Width}.");
    }
}
