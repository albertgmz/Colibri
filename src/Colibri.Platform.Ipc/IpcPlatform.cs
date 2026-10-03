using Colibri.Core.Platform;

namespace Colibri.Platform.Ipc;

/// <summary>
/// Chooses the implementations for the current OS. Both are needed before dependency injection exists (the
/// app takes the single-instance mutex first thing) and by the native-messaging host, which has none, so
/// they are created here rather than in <c>AddColibriPlatform</c>.
/// </summary>
public static class IpcPlatform
{
    public static IIpcEndpointProvider CreateEndpointProvider()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsIpcEndpointProvider();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new UserNameIpcEndpointProvider();
        }

        return new LinuxIpcEndpointProvider();
    }

    public static IForegroundHandoff CreateForegroundHandoff() =>
        OperatingSystem.IsWindows() ? new WindowsForegroundHandoff() : new NoForegroundHandoff();
}
