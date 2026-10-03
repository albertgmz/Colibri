using Colibri.App.Resources;
using Colibri.App.Services;
using Colibri.App.ViewModels;
using Colibri.Core.Abstractions;
using Colibri.Core.Engine;
using Colibri.Core.Platform;
using Colibri.Core.Services;
using Colibri.Core.Settings;
using Colibri.Engine.Aria2;
using Colibri.Platform.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Colibri.App;

public static class AppServices
{
    /// <summary>The notification texts in the user's language, for <c>AddColibriPlatform</c>.</summary>
    public static NotificationTexts CreateNotificationTexts() => new()
    {
        DownloadCompleteTitle = Strings.NotificationCompleted,
        DownloadFailedTitle = Strings.NotificationFailed,
        Open = Strings.NotificationOpen,
        ShowInFolder = Strings.NotificationShowInFolder,
        Retry = Strings.NotificationRetry,
    };

    /// <summary>
    /// Registers Colibri's own services (storage, engine, download manager, view models). The platform
    /// services come from <c>AddColibriPlatform</c>.
    /// </summary>
    public static IServiceCollection AddColibriApp(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsStore>(sp =>
            new JsonSettingsStore(sp.GetRequiredService<IAppPaths>().SettingsPath, sp.GetRequiredService<ILogger<JsonSettingsStore>>()));

        // Loaded once and shared. Program resolves it before the UI starts, so this blocking load never
        // runs on the UI thread.
        services.AddSingleton(sp => sp.GetRequiredService<ISettingsStore>().LoadAsync(CancellationToken.None).GetAwaiter().GetResult());

        services.AddSingleton<IDownloadRepository>(sp => new SqliteDownloadRepository(sp.GetRequiredService<IAppPaths>().DatabasePath));
        services.AddSingleton<ILinkResolver, DirectLinkResolver>();
        services.AddSingleton<LinkResolverPipeline>();

        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AppSettings>();

            // Read on every engine start, so changed settings apply the next time aria2 starts.
            Aria2Settings ReadSettings() => new(
                settings.Aria2Path,
                new EngineOptions(settings.MaxConcurrentDownloads, settings.ConnectionsPerServer, settings.GlobalSpeedLimitKiB * 1024L));

            return new Aria2Engine(
                sp.GetRequiredService<IAria2Locator>(), sp.GetRequiredService<IAppPaths>(), sp.GetRequiredService<ILogger<Aria2Engine>>(), ReadSettings);
        });
        services.AddSingleton<IDownloadEngine>(sp => sp.GetRequiredService<Aria2Engine>());

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<DownloadManager>();

        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<DownloadNotifier>();

        // Its IBrowserHostRegistrar parameter is optional (tests leave it out; the page then hides the
        // browser integration status); AddColibriPlatform registers one on every OS.
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        // The window frame depends on the OS. IWindowChrome uses Avalonia types, so it cannot live in
        // Colibri.Platform; this is the one OS check in the app project.
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IWindowChrome, WindowsWindowChrome>();
        }
        else
        {
            services.AddSingleton<IWindowChrome, DefaultWindowChrome>();
        }

        return services;
    }
}
