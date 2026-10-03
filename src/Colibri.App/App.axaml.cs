using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Colibri.App.Services;
using Colibri.App.ViewModels;
using Colibri.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Colibri.App;

public partial class App : Application
{
    /// <summary>
    /// The app's services, set by <see cref="Program"/> before the framework initializes. Null in the
    /// XAML designer and in headless tests, which create their windows themselves.
    /// </summary>
    public IServiceProvider? Services { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Services is { } services)
        {
            var logger = services.GetRequiredService<ILogger<App>>();
            Dispatcher.UIThread.UnhandledException += (_, e) =>
                logger.LogCritical(e.Exception, "Unhandled exception on the UI thread");

            // Colibri keeps running with its window hidden in the tray; DesktopShell.ExitAsync ends it.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            ThemeService.Apply(services.GetRequiredService<AppSettings>().Theme);
            ThemeService.ApplyAccent(services.GetRequiredService<AppSettings>().AccentColor);

            var viewModel = services.GetRequiredService<MainWindowViewModel>();
            var shell = ActivatorUtilities.CreateInstance<DesktopShell>(services, desktop);
            var initialized = viewModel.InitializeAsync();

            // Posted, so it runs once the lifetime has started: a MainWindow set before that would be shown
            // by the lifetime itself, even when Colibri should start hidden in the tray.
            var minimized = WindowBehavior.HasMinimizedArgument(desktop.Args);
            var notifier = services.GetRequiredService<DownloadNotifier>();
            Dispatcher.UIThread.Post(() => _ = StartShellAsync(shell, minimized, initialized, notifier, logger));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartShellAsync(DesktopShell shell, bool minimized, Task initialized, DownloadNotifier notifier, ILogger logger)
    {
        try
        {
            await shell.StartAsync(minimized);

            // Notification actions need the downloads (open, retry) and the window (show).
            await initialized;
            notifier.StartHandlingActions(shell.ShowMainWindow);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "The main window could not be started");
            await shell.ExitAsync();
        }
    }
}
