using Colibri.Core.Platform;
using Colibri.Platform.Aria2;
using Colibri.Platform.Autostart;
using Colibri.Platform.BrowserHost;
using Colibri.Platform.DBus;
using Colibri.Platform.Notifications;
using Colibri.Platform.Paths;
using Colibri.Platform.Security;
using Colibri.Platform.Shell;
using Colibri.Platform.Taskbar;
using Colibri.Platform.Tray;
using Colibri.Platform.Updates;
using Colibri.Core.Updates;
using Microsoft.Extensions.DependencyInjection;

namespace Colibri.Platform;

public static class PlatformServiceCollectionExtensions
{
    /// <summary>
    /// Registers the implementations of the Core platform interfaces for the current OS. This is the
    /// only place that chooses an implementation by operating system, except for the local pipe names and
    /// the foreground handoff, which are needed before this runs (see <c>IpcPlatform</c> in Colibri.Platform.Ipc).
    /// </summary>
    /// <param name="services">The app's service collection.</param>
    /// <param name="notificationTexts">
    /// Translated notification texts from the app's resources; English defaults when null.
    /// </param>
    public static IServiceCollection AddColibriPlatform(this IServiceCollection services, NotificationTexts? notificationTexts = null)
    {
        services.AddSingleton(notificationTexts ?? new NotificationTexts());

        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<ICredentialProtector, WindowsCredentialProtector>();
            services.AddSingleton<IDownloadsFolderLocator, WindowsDownloadsFolderLocator>();
            services.AddSingleton<IVolumeInfoService, WindowsVolumeInfoService>();
            services.AddSingleton<IAria2Locator, WindowsAria2Locator>();
            services.AddSingleton<IShellService, WindowsShellService>();
            services.AddSingleton<IAutostartService, WindowsAutostartService>();
            services.AddSingleton<ITaskbarProgress, WindowsTaskbarProgress>();
            services.AddSingleton<ITrayAvailability, AlwaysTrayAvailability>();
            services.AddSingleton<IBrowserHostRegistrar, WindowsBrowserHostRegistrar>();
#if WINDOWS
            services.AddSingleton<INotificationService, WindowsToastNotificationService>();
#else
            // Toasts need the Windows target framework, which the app always uses on Windows. Only other
            // consumers of the plain net10.0 build (such as tests) get here.
            services.AddSingleton<INotificationService, NoNotificationService>();
#endif
        }
        else if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<ICredentialProtector, MacCredentialProtector>();
            services.AddSingleton<IDownloadsFolderLocator, MacDownloadsFolderLocator>();
            services.AddSingleton<IVolumeInfoService, MacVolumeInfoService>();

            // Apps started from Finder or the Dock do not get the PATH of the user's shell, so Homebrew's
            // folders (Apple silicon, then Intel) are searched explicitly.
            services.AddSingleton<IAria2Locator>(new UnixAria2Locator(["/opt/homebrew/bin", "/usr/local/bin"]));
            services.AddSingleton<IShellService, MacShellService>();
            services.AddSingleton<IAutostartService, MacAutostartService>();
            services.AddSingleton<ITaskbarProgress, NoTaskbarProgress>();
            services.AddSingleton<ITrayAvailability, AlwaysTrayAvailability>();
            services.AddSingleton<INotificationService, MacNotificationService>();
            services.AddSingleton<IBrowserHostRegistrar>(new UnixBrowserHostRegistrar(UnixBrowserHostRegistrar.MacFolders(HomeFolder())));
        }
        else
        {
            services.AddSingleton<ICredentialProtector, LinuxCredentialProtector>();
            services.AddSingleton<IDownloadsFolderLocator, XdgDownloadsFolderLocator>();
            services.AddSingleton<IVolumeInfoService, LinuxVolumeInfoService>();
            services.AddSingleton<IAria2Locator>(new UnixAria2Locator([]));
            services.AddSingleton<SessionBus>();
            services.AddSingleton<IShellService, LinuxShellService>();
            services.AddSingleton<IAutostartService, LinuxAutostartService>();
            services.AddSingleton<ITaskbarProgress, NoTaskbarProgress>();
            services.AddSingleton<ITrayAvailability, LinuxTrayAvailability>();
            services.AddSingleton<INotificationService, LinuxNotificationService>();
            services.AddSingleton<IBrowserHostRegistrar>(new UnixBrowserHostRegistrar(
                UnixBrowserHostRegistrar.LinuxFolders(HomeFolder(), Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"))));
        }

        services.AddSingleton<IAppPaths, AppPaths>();
        services.AddSingleton<Colibri.Core.Network.INetworkInterfaceService, Colibri.Platform.Network.NetworkInterfaceService>();
        services.AddSingleton<IReleaseUpdateService>(sp =>
        {
            var kind = UpdatePackageKind.None;
            if (OperatingSystem.IsWindows())
            {
                var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Colibri");
                kind = WindowsUpdateHandoff.IsOwnedInstallation(Environment.ProcessPath ?? "", installed)
                    ? UpdatePackageKind.Installer : UpdatePackageKind.Portable;
            }
            var version = typeof(PlatformServiceCollectionExtensions).Assembly.GetName().Version!.ToString(3);
            return new GitHubReleaseUpdateService(GitHubReleaseUpdateService.CreateClient(), version, kind,
                Path.Combine(sp.GetRequiredService<IAppPaths>().DataDirectory, "updates"),
                installer: kind == UpdatePackageKind.Installer ? WindowsUpdateHandoff.PrepareAsync : null);
        });
        return services;
    }

    private static string HomeFolder() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
