using Colibri.Core.Platform;

namespace Colibri.App.Services;

/// <summary>What "Install / repair" registers with the browsers: the native-messaging host next to this app.</summary>
public static class NativeHostSetup
{
    public static NativeHostRegistration CreateRegistration() => new()
    {
        Description = "Colibri download manager",

        // The build and publish copy Colibri.NativeHost next to Colibri. It has the same file extension as
        // this process (".exe" on Windows, none elsewhere).
        HostExecutablePath = Path.Combine(
            AppContext.BaseDirectory, "Colibri.NativeHost" + Path.GetExtension(Environment.ProcessPath ?? string.Empty)),
        AllowedOrigins = [BrowserExtension.AllowedOrigin],
    };
}
