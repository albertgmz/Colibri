namespace Colibri.Core.Engine;

/// <summary>
/// Engine-wide download settings.
/// </summary>
/// <param name="MaxConcurrentDownloads">How many downloads may transfer at the same time.</param>
/// <param name="ConnectionsPerServer">Maximum connections to one server per download (applies to downloads added afterwards).</param>
/// <param name="GlobalSpeedLimitBytesPerSecond">Combined download limit in bytes per second; 0 means unlimited.</param>
public sealed record EngineOptions(int MaxConcurrentDownloads, int ConnectionsPerServer, long GlobalSpeedLimitBytesPerSecond);
