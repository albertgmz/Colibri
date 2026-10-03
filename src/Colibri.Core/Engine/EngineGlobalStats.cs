namespace Colibri.Core.Engine;

/// <summary>
/// Totals across all downloads of an engine.
/// </summary>
/// <param name="DownloadSpeed">Combined speed in bytes per second.</param>
/// <param name="NumActive">Downloads currently transferring.</param>
/// <param name="NumWaiting">Downloads queued or paused in the engine.</param>
public sealed record EngineGlobalStats(long DownloadSpeed, int NumActive, int NumWaiting);
