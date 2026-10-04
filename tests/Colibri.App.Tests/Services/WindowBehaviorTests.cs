using Colibri.App.Services;

namespace Colibri.App.Tests.Services;

public class WindowBehaviorTests
{
    [Theory]
    [InlineData(true, true, CloseAction.HideToTray)]
    [InlineData(true, false, CloseAction.Exit)]
    [InlineData(false, true, CloseAction.Exit)] // No tray: a hidden window could never come back.
    [InlineData(false, false, CloseAction.Exit)]
    public void Close_hides_to_the_tray_only_when_a_tray_exists_and_the_setting_is_on(bool trayAvailable, bool closeToTray, CloseAction expected)
    {
        Assert.Equal(expected, WindowBehavior.OnClose(trayAvailable, closeToTray));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Minimize_hides_to_the_tray_only_when_a_tray_exists_and_the_setting_is_on(bool trayAvailable, bool minimizeToTray, bool expected)
    {
        Assert.Equal(expected, WindowBehavior.HideOnMinimize(trayAvailable, minimizeToTray));
    }

    [Theory]
    [InlineData(false, true, StartupWindowMode.Shown)]
    [InlineData(false, false, StartupWindowMode.Shown)]
    [InlineData(true, true, StartupWindowMode.HiddenInTray)]
    [InlineData(true, false, StartupWindowMode.Minimized)]
    public void Minimized_argument_starts_in_the_tray_or_minimized_without_one(bool minimized, bool trayAvailable, StartupWindowMode expected)
    {
        Assert.Equal(expected, WindowBehavior.OnStartup(minimized, trayAvailable));
    }

    [Fact]
    public void Recognizes_the_minimized_argument()
    {
        Assert.True(WindowBehavior.HasMinimizedArgument(["--minimized"]));
        Assert.True(WindowBehavior.HasMinimizedArgument(["x", "--MINIMIZED"]));
        Assert.False(WindowBehavior.HasMinimizedArgument(["--min"]));
        Assert.False(WindowBehavior.HasMinimizedArgument(null));
    }
}
