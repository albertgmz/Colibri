namespace Colibri.Core.Engine;

/// <summary>
/// Health of a download engine process.
/// </summary>
public enum EngineState
{
    Stopped,
    Starting,
    Running,
    Restarting,
    Failed,

    /// <summary>The engine executable could not be found.</summary>
    NotFound,
}
