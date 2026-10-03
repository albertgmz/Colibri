using System.Runtime.Versioning;
using System.Security.Principal;
using Colibri.Core.Ipc;
using Colibri.Core.Platform;

namespace Colibri.Platform.Ipc;

/// <summary>
/// Windows: names from a hash of the user's security identifier (SID). Pipe names and the mutex (which is
/// machine-wide, so that every session of a user shares it) are visible to all users of the machine; two
/// accounts with the same user name (a domain "bob" and a local "bob") have different SIDs, so they never
/// compete for the same names.
/// </summary>
internal sealed class WindowsIpcEndpointProvider : IIpcEndpointProvider
{
    [SupportedOSPlatform("windows")]
    public IpcEndpoint GetEndpoint()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return FromSid(identity.User?.Value, Environment.UserName);
    }

    /// <summary>The SID's names; the user name's when there is no SID (not expected for a signed-in user).</summary>
    internal static IpcEndpoint FromSid(string? sid, string userName) =>
        IpcEndpoint.ForUser(string.IsNullOrEmpty(sid) ? userName : sid);
}
