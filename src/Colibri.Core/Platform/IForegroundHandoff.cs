using System.IO.Pipes;

namespace Colibri.Core.Platform;

/// <summary>
/// Lets the running Colibri bring its window to the front for a request from the browser. Used by the
/// native-messaging host, which the browser (the app in front) started.
/// </summary>
public interface IForegroundHandoff
{
    /// <summary>
    /// Allows the process at the other end of <paramref name="connectedPipe"/> (Colibri) to take the
    /// foreground. Returns false when that could not be granted; the window may then open behind the browser.
    /// </summary>
    bool AllowAppToTakeForeground(PipeStream connectedPipe);
}
