namespace Colibri.Core.Engine;

/// <summary>
/// Thrown by an <see cref="IDownloadEngine"/> when it refuses an operation, for example pausing a
/// download that is already paused or calling it while the engine is not running. The download
/// and the engine are still usable; callers should show or log the message and carry on.
/// </summary>
public sealed class EngineOperationException : Exception
{
    public EngineOperationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
