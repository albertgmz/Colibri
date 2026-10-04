namespace Colibri.Core.Abstractions;

/// <summary>Explicitly discards stored credentials without deleting download history.</summary>
public interface IDownloadCredentialRepository
{
    Task ClearCredentialsAsync(Guid downloadId, CancellationToken ct);
}
