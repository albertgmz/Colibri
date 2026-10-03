using Colibri.Core.Ipc;

namespace Colibri.Core.Platform;

/// <summary>
/// The local pipe and mutex names of the current user. The app and the browser's native-messaging host
/// must get the same answer, so both use the same provider.
/// </summary>
public interface IIpcEndpointProvider
{
    IpcEndpoint GetEndpoint();
}
