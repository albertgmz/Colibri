using Avalonia;
using Avalonia.Headless;
using Colibri.App.Tests;

// Runs every [AvaloniaFact] in this assembly on the headless platform (no display needed).
[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Colibri.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        // The fake drawing backend gives every glyph the same width, which cannot verify real layouts.
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
