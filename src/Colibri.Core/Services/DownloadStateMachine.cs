using Colibri.Core.Models;

namespace Colibri.Core.Services;

/// <summary>
/// The single place that defines which <see cref="DownloadState"/> changes are allowed.
/// Changing to the same state is always allowed (a no-op).
/// </summary>
public static class DownloadStateMachine
{
    // Non-obvious entries:
    // - Queued -> Completed/Failed: the engine can finish or reject a download before we ever see it Active.
    // - Active -> Queued: the engine puts a download back in its waiting queue (e.g. the concurrency limit dropped).
    // - Paused -> Completed: a pause can race the last bytes; the engine (or startup reconcile) reports Complete.
    // - Failed -> Active: aria2 restores errored downloads from its session file and may restart them itself.
    // - Completed is terminal: downloading again creates a new item.
    private static readonly Dictionary<DownloadState, DownloadState[]> Allowed = new()
    {
        [DownloadState.Queued] = [DownloadState.Active, DownloadState.Paused, DownloadState.Failed, DownloadState.Completed],
        [DownloadState.Active] = [DownloadState.Paused, DownloadState.Completed, DownloadState.Failed, DownloadState.Queued],
        [DownloadState.Paused] = [DownloadState.Queued, DownloadState.Active, DownloadState.Failed, DownloadState.Completed],
        [DownloadState.Failed] = [DownloadState.Queued, DownloadState.Active],
        [DownloadState.Completed] = [],
    };

    /// <summary>Whether a download may move from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static bool CanTransition(DownloadState from, DownloadState to) =>
        from == to || (Allowed.TryGetValue(from, out var targets) && targets.Contains(to));

    /// <summary>Throws <see cref="InvalidOperationException"/> if the change is not allowed.</summary>
    public static void EnsureTransition(DownloadState from, DownloadState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"A download cannot change from {from} to {to}.");
        }
    }
}
