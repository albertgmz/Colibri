using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Colibri.Core.Platform;

namespace Colibri.Platform.Ipc;

/// <summary>
/// Windows lets a process bring its window to the front only in some cases, for example when it is the
/// foreground app or was started by it; otherwise the window opens behind the app in front and its taskbar
/// button flashes. The native-messaging host was started by the browser, which is in front when the user
/// starts a download, so the host may pass that right on with <c>AllowSetForegroundWindow</c>. It passes it
/// to Colibri's process only (found from the connected pipe), not to every process (<c>ASFW_ANY</c>). The
/// right lasts until the user's next input elsewhere.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsForegroundHandoff : IForegroundHandoff
{
    public bool AllowAppToTakeForeground(PipeStream connectedPipe) =>
        TryGetServerProcessId(connectedPipe, out var processId) && AllowSetForegroundWindow(processId);

    /// <summary>The process that created the pipe server at the other end of a connected client pipe.</summary>
    internal static bool TryGetServerProcessId(PipeStream connectedPipe, out uint processId) =>
        GetNamedPipeServerProcessId(connectedPipe.SafePipeHandle, out processId);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
