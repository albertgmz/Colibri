using Colibri.Core.Models;

namespace Colibri.Core.Engine;

/// <summary>
/// Something that actually transfers bytes (for v1: aria2).
/// </summary>
public interface IDownloadEngine
{
    /// <summary>Stable engine id, stored on each <see cref="DownloadItem"/>.</summary>
    string Id { get; }

    /// <summary>Current health of the engine.</summary>
    EngineState State { get; }

    /// <summary>Raised when a download starts, pauses, stops, completes or fails.</summary>
    event EventHandler<EngineDownloadEvent>? DownloadEvent;

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    event EventHandler<EngineState>? StateChanged;

    /// <summary>Whether this engine can download <paramref name="request"/>.</summary>
    bool CanHandle(DownloadRequest request);

    /// <summary>Starts the engine (for aria2: launches and connects to the process).</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Stops the engine, keeping its session so downloads can resume later.</summary>
    Task StopAsync(CancellationToken ct);

    /// <summary>Adds a download and returns the engine handle.</summary>
    Task<string> AddAsync(DownloadRequest request, string saveFolder, string fileName, CancellationToken ct);

    /// <summary>Pauses a download.</summary>
    Task PauseAsync(string handle, CancellationToken ct);

    /// <summary>Resumes a paused download.</summary>
    Task ResumeAsync(string handle, CancellationToken ct);

    /// <summary>Removes a download from the engine (does not delete the file).</summary>
    Task RemoveAsync(string handle, CancellationToken ct);

    /// <summary>Returns the status of one download, or null if the engine does not know it.</summary>
    Task<EngineDownloadStatus?> GetStatusAsync(string handle, CancellationToken ct);

    /// <summary>Returns every download the engine knows: active, waiting, paused and stopped.</summary>
    Task<IReadOnlyList<EngineDownloadStatus>> GetAllAsync(CancellationToken ct);

    /// <summary>Returns totals across all downloads.</summary>
    Task<EngineGlobalStats> GetGlobalStatsAsync(CancellationToken ct);
}
