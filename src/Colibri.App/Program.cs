using Avalonia;
using Colibri.App.Services;
using Colibri.Core.Ipc;
using Colibri.Core.Platform;
using Colibri.Core.Settings;
using Colibri.Platform;
using Colibri.Platform.Ipc;
using Colibri.Platform.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace Colibri.App;

public static class Program
{
    /// <summary>
    /// How long a new Colibri keeps trying when another one holds the single-instance mutex but does not accept
    /// its arguments: that one is still starting (its pipe opens last) or exiting (the mutex is released when
    /// its process ends).
    /// </summary>
    private static readonly TimeSpan WaitForExitingInstance = TimeSpan.FromSeconds(15);

    // Don't use any Avalonia, third-party APIs or any SynchronizationContext-reliant code before
    // StartWithClassicDesktopLifetime is called: things aren't initialized yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // Only one Colibri runs per user. A second start hands its arguments to the running one (which
        // shows its window) and exits before starting anything, aria2 included. A Colibri that is still
        // exiting is waited for and then replaced. The guard is released when Main returns, on this same
        // thread as the mutex requires.
        var endpoint = IpcPlatform.CreateEndpointProvider().GetEndpoint();
        var election = InstanceElection.Run(endpoint.MutexName, () => ForwardToPrimary(endpoint.PipeName, args), WaitForExitingInstance);
        using var primary = election.Guard;
        if (election.Role == InstanceRole.Secondary)
        {
            return 0;
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
        // Defer translated notification text creation until the saved startup UI culture is applied.
        builder.Services.Replace(ServiceDescriptor.Singleton<NotificationTexts>(_ => AppServices.CreateNotificationTexts()));
        builder.Services.AddSingleton(endpoint);

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILogger<App>>();

        if (election.Role == InstanceRole.NoAnswer)
        {
            logger.LogError(
                "Another Colibri holds the single-instance lock but neither took this start's arguments nor exited within {Seconds} s; this start is given up",
                WaitForExitingInstance.TotalSeconds);
            return 1;
        }

        if (election.Role == InstanceRole.PrimaryAfterWaiting)
        {
            logger.LogInformation("Waited for the previous Colibri to exit");
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        logger.LogInformation("Colibri {Version} starting", typeof(Program).Assembly.GetName().Version);

        // Load the settings now, while no UI thread exists yet (loading is async; see AppServices).
        var startupSettings = host.Services.GetRequiredService<AppSettings>();
        LanguageService.ApplyStartup(startupSettings.Language);

        // Create the notification service and listen to it before anything slow happens. When a Windows toast
        // is clicked after Colibri exited, Windows starts Colibri ("-ToastActivated -Embedding", ignored like
        // any unknown argument) and delivers the click as soon as the toast service exists; the notifier
        // keeps it until the app is ready (see DownloadNotifier).
        host.Services.GetRequiredService<DownloadNotifier>().Start();

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

    /// <summary>Sends this start's arguments to the running Colibri; null when it does not answer (it may be starting or exiting).</summary>
    private static IpcResponse? ForwardToPrimary(string pipeName, string[] args)
    {
        try
        {
            return LocalPipeClient.SendAsync(pipeName, new ActivateRequest(args), LocalPipeClient.DefaultTimeout, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Avalonia configuration, don't remove; also used by the visual designer and the headless tests.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
