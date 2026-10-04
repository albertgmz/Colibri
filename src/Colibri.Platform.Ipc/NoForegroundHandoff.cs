using System.IO.Pipes;
using Colibri.Core.Platform;

namespace Colibri.Platform.Ipc;

/// <summary>
/// Linux and macOS: nothing to hand over. Neither has Windows' rule that only the foreground app may raise
/// its windows; whether a newly shown window comes to the front is up to the window manager (some show
/// "Colibri is ready" instead), and the app's own Activate and Topmost request is all it can do.
/// </summary>
internal sealed class NoForegroundHandoff : IForegroundHandoff
{
    public bool AllowAppToTakeForeground(PipeStream connectedPipe) => true;
}
