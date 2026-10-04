using Colibri.Core.Models;

namespace Colibri.Core.Engine;

/// <summary>
/// Something that actually transfers bytes (for v1: aria2).
/// </summary>
/// <remarks>
/// Operations on a single download throw <see cref="EngineOperationException"/> when the engine refuses
/// them (wrong state, engine not running). Lost connections and timeouts surface as
/// <see cref="IOException"/> and <see cref="TimeoutException"/>.
/// </remarks>
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

    /// <summary>
    /// Applies engine-wide settings. Concurrency and the speed limit apply at once when the engine is
    /// running; connections per server apply to downloads added afterwards. The options are also kept
    /// for the next start or restart.
    /// </summary>
    Task ApplyOptionsAsync(EngineOptions options, CancellationToken ct);

    /// <summary>
    /// Returns a new, unused handle. The caller stores it before calling <see cref="AddAsync"/> with it, so
    /// a download whose add timed out (and may still have reached the engine) can be found again by it.
    /// </summary>
    string CreateHandle();

    /// <summary>Adds a download and returns the engine handle.</summary>
    /// <param name="request">What to download.</param>
    /// <param name="saveFolder">Folder the file is written to.</param>
    /// <param name="fileName">Output file name (already sanitized and unique).</param>
    /// <param name="handle">
    /// Handle to reuse, or null for a new one. Re-adding a download the engine has forgotten with its old
    /// handle and the same folder and name keeps the handle stable and resumes the partial file.
    /// </param>
    /// <param name="startPaused">Add the download paused instead of queuing it.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> AddAsync(DownloadRequest request, string saveFolder, string fileName, string? handle, bool startPaused, CancellationToken ct);

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

    /// <summary>Optional live server and transfer-option telemetry.</summary>
    Task<EngineDownloadDetails?> GetDetailsAsync(string handle, CancellationToken ct) =>
        Task.FromResult<EngineDownloadDetails?>(null);

    Task ApplyDownloadOptionsAsync(string handle, DownloadTransferOptions options, CancellationToken ct) =>
        throw new EngineOperationException("This engine does not support per-download options.");

    /// <summary>Pauses the transfer and changes its network policy. The caller persists it before resuming.</summary>
    Task ApplyNetworkPolicyAsync(string handle, Colibri.Core.Network.DownloadNetworkPolicy? policy, CancellationToken ct) =>
        throw new EngineOperationException("This engine does not support per-download network policies.");
}
