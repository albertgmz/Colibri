using Avalonia;
using Colibri.Core.Ipc;
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
        // Only one Colibri runs per user. A second start hands its arguments to the running one (which
        // shows its window) and exits before starting anything, aria2 included. The guard is released
        // when Main returns, on this same thread as the mutex requires.
        using var primary = SingleInstanceGuard.TryAcquire(IpcProtocol.InstanceMutexName);
        if (primary is null)
        {
            return ForwardToPrimary(args);
        }

        // The generic host provides dependency injection, configuration and logging. It is not started:
        // Avalonia's lifetime runs the app, and DesktopShell.ExitAsync stops the downloads.
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

    private static int ForwardToPrimary(string[] args)
    {
        try
        {
            var response = LocalPipeClient.SendAsync(IpcProtocol.DefaultPipeName, new ActivateRequest(args), LocalPipeClient.DefaultTimeout, CancellationToken.None)
                .GetAwaiter().GetResult();
            return response.Ok ? 0 : 1;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // The running Colibri did not answer (it may be exiting). Nothing else to do.
            Console.Error.WriteLine($"Colibri is already running but did not answer: {ex.Message}");
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
