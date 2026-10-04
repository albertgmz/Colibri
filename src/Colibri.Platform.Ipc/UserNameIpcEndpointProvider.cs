using Colibri.Core.Ipc;
using Colibri.Core.Platform;

namespace Colibri.Platform.Ipc;

/// <summary>
/// Names from a hash of the user name. Used on macOS, and as the fallback elsewhere.
/// </summary>
/// <remarks>
/// On macOS the socket goes to <c>$TMPDIR/CoreFxPipe_colibri-&lt;16 hex&gt;</c>. <c>$TMPDIR</c> is a per-user
/// folder only its owner can enter (<c>/var/folders/xx/&lt;random&gt;/T/</c>, about 50 characters), so another
/// user cannot create the socket first, and the whole path (about 85 characters) stays within the 103
/// characters a macOS socket path may have.
/// </remarks>
internal sealed class UserNameIpcEndpointProvider : IIpcEndpointProvider
{
    public IpcEndpoint GetEndpoint() => IpcEndpoint.ForUser(Environment.UserName);
}
