namespace Colibri.Core.Engine;

/// <summary>Ownership cannot be released because the engine transfer could not be confirmed stopped.</summary>
public sealed class DownloadCleanupException : Exception
{
    public DownloadCleanupException() : base("The transfer could not be stopped safely. Keep the browser download paused and try again.") { }
}
