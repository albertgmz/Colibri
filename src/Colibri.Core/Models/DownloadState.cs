namespace Colibri.Core.Models;

/// <summary>
/// Lifecycle state of a download. Allowed changes are defined in <see cref="Services.DownloadStateMachine"/>.
/// </summary>
public enum DownloadState
{
    Queued,
    Active,
    Paused,
    Completed,
    Failed,
}
