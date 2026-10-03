using Colibri.Core.Platform;
using Colibri.Platform.Aria2;
using Colibri.Platform.Paths;
using Colibri.Platform.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace Colibri.Platform;

public static class PlatformServiceCollectionExtensions
{
    /// <summary>
    /// Registers the implementations of the Core platform interfaces for the current OS. This is the
    /// only place that chooses an implementation by operating system.
    /// </summary>
    public static IServiceCollection AddColibriPlatform(this IServiceCollection services)
    {
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IDownloadsFolderLocator, WindowsDownloadsFolderLocator>();
            services.AddSingleton<IAria2Locator, WindowsAria2Locator>();
        }
        else if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<IDownloadsFolderLocator, MacDownloadsFolderLocator>();

            // Apps started from Finder or the Dock do not get the PATH of the user's shell, so Homebrew's
            // folders (Apple silicon, then Intel) are searched explicitly.
            services.AddSingleton<IAria2Locator>(new UnixAria2Locator(["/opt/homebrew/bin", "/usr/local/bin"]));
        }
        else
        {
            services.AddSingleton<IDownloadsFolderLocator, XdgDownloadsFolderLocator>();
            services.AddSingleton<IAria2Locator>(new UnixAria2Locator([]));
        }

        services.AddSingleton<IAppPaths, AppPaths>();
        services.AddSingleton<IShellService, ProcessShellService>();
        return services;
    }
}
