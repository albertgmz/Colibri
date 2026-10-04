namespace Colibri.Core.Engine;

/// <summary>
/// Kind of lifecycle event raised by an engine for one download.
/// </summary>
public enum EngineDownloadEventKind
{
    Started,
    Paused,
    Stopped,
    Completed,
    Error,
    ContentCompleted,
}
