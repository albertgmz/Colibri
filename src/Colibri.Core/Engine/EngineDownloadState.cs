namespace Colibri.Core.Engine;

/// <summary>
/// State of a download as reported by the engine (mirrors aria2's status values).
/// </summary>
public enum EngineDownloadState
{
    Active,
    Waiting,
    Paused,
    Complete,
    Error,
    Removed,
}
