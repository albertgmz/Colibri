using Avalonia;
using Colibri.Core.Platform;
using Colibri.Core.Settings;
using Colibri.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace Colibri.App;

public static class Program
{
    // Don't use any Avalonia, third-party APIs or any SynchronizationContext-reliant code before
    // StartWithClassicDesktopLifetime is called: things aren't initialized yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // The generic host provides dependency injection, configuration and logging. It is not started:
        // Avalonia's lifetime runs the app, and the main window's closing stops the downloads.
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog((services, logging) => logging
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .WriteTo.File(
                Path.Combine(services.GetRequiredService<IAppPaths>().LogsDirectory, "colibri-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));
        builder.Services.AddColibriPlatform();
        builder.Services.AddColibriApp();

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILogger<App>>();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        logger.LogInformation("Colibri {Version} starting", typeof(Program).Assembly.GetName().Version);

        // Load the settings now, while no UI thread exists yet (loading is async; see AppServices).
        host.Services.GetRequiredService<AppSettings>();

        try
        {
            BuildAvaloniaApp()
                .AfterSetup(app => ((App)app.Instance!).Services = host.Services)
                .StartWithClassicDesktopLifetime(args);
            logger.LogInformation("Colibri exited");
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Colibri crashed");
            return 1;
        }
    }

    // Avalonia configuration, don't remove; also used by the visual designer and the headless tests.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
