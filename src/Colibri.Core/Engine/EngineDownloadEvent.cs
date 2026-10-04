namespace Colibri.Core.Engine;

/// <summary>
/// Lifecycle event for one download, raised by an engine.
/// </summary>
/// <param name="Handle">Engine handle of the download.</param>
/// <param name="Kind">What happened.</param>
public sealed record EngineDownloadEvent(string Handle, EngineDownloadEventKind Kind);
