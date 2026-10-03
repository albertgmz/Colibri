using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Colibri.App.Services;
using Colibri.App.ViewModels;
using Colibri.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Colibri.App;

public partial class App : Application
{
    private bool _shutdownStarted;
    private bool _shutdownFinished;

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

            // Closing the main window exits, even if an Add URL window is still open.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            var viewModel = services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = viewModel };
            services.GetRequiredService<IWindowChrome>().Apply(window);
            window.Closing += (_, e) => OnMainWindowClosing(window, viewModel, e, logger);
            desktop.MainWindow = window;

            _ = viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The first close is cancelled so the download manager can stop asynchronously (save progress,
    /// let aria2 save its session); the window then closes for real. Waiting with .Wait() here would
    /// block the UI thread that the shutdown itself needs.
    /// </summary>
    private async void OnMainWindowClosing(Window window, MainWindowViewModel viewModel, WindowClosingEventArgs e, ILogger logger)
    {
        if (_shutdownFinished)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        window.Hide();
        try
        {
            await viewModel.ShutdownAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stopping the download manager failed");
        }

        _shutdownFinished = true;
        window.Close();
    }
}
