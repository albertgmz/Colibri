using System.Security.Cryptography;
using System.Text;

namespace Colibri.Core.Ipc;

/// <summary>
/// Where the Colibri processes of one user find each other: the local pipe the running Colibri listens on,
/// and the named mutex its primary instance holds. Built by the platform's <c>IIpcEndpointProvider</c>.
/// </summary>
/// <param name="PipeName">
/// A pipe name, or on Linux and macOS also an absolute socket path: .NET uses a rooted name as the socket
/// file itself, and puts any other name in the temp folder as <c>$TMPDIR/CoreFxPipe_&lt;name&gt;</c>.
/// </param>
/// <param name="MutexName">Name of the mutex that marks the primary Colibri instance.</param>
public sealed record IpcEndpoint(string PipeName, string MutexName)
{
    /// <summary>
    /// "colibri-" and "colibri-instance-" followed by the first 16 hex digits of the SHA-256 of
    /// <paramref name="userKey"/> (a user name or a Windows SID): short, and only characters any pipe or
    /// mutex name can hold.
    /// </summary>
    public static IpcEndpoint ForUser(string userKey)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userKey)), 0, 8);
        return new IpcEndpoint("colibri-" + hash, "colibri-instance-" + hash);
    }
}
