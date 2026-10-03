using System.Text;
using Colibri.Core.Ipc;
using Colibri.Core.Platform;

namespace Colibri.Platform.Ipc;

/// <summary>
/// Linux: the socket is <c>$XDG_RUNTIME_DIR/colibri.sock</c>. That folder (usually <c>/run/user/&lt;uid&gt;</c>)
/// belongs to the user and only they can enter it (0700), so another user cannot create the socket first
/// and block Colibri, as they could in the shared <c>/tmp</c>. Without a usable <c>$XDG_RUNTIME_DIR</c> the
/// socket stays in the temp folder (<see cref="UserNameIpcEndpointProvider"/>).
/// </summary>
/// <remarks>
/// .NET uses a rooted pipe name as the socket path itself (see <c>PipeStream.Unix.cs</c>, <c>GetPipePath</c>);
/// any other name becomes <c>$TMPDIR/CoreFxPipe_&lt;name&gt;</c>. The mutex keeps its name: with
/// <c>CurrentUserOnly</c>, .NET gives each Unix user a namespace of their own for mutex names.
/// </remarks>
internal sealed class LinuxIpcEndpointProvider : IIpcEndpointProvider
{
    /// <summary>Longest socket path Linux accepts, in bytes: <c>sun_path</c> holds 108 including the final zero.</summary>
    internal const int MaxSocketPathBytes = 107;

    internal const string SocketFileName = "colibri.sock";

    public IpcEndpoint GetEndpoint() =>
        Derive(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), Environment.UserName, Directory.Exists);

    internal static IpcEndpoint Derive(string? xdgRuntimeDir, string userName, Func<string, bool> directoryExists)
    {
        var fallback = IpcEndpoint.ForUser(userName);

        // The XDG spec: an unset or relative value must be ignored.
        if (string.IsNullOrEmpty(xdgRuntimeDir) || !Path.IsPathRooted(xdgRuntimeDir) || !directoryExists(xdgRuntimeDir))
        {
            return fallback;
        }

        var socketPath = Path.Combine(xdgRuntimeDir, SocketFileName);
        return Encoding.UTF8.GetByteCount(socketPath) > MaxSocketPathBytes
            ? fallback
            : fallback with { PipeName = socketPath };
    }
}
