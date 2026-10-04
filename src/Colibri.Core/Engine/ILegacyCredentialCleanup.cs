namespace Colibri.Core.Engine;

/// <summary>Optional engine cleanup performed only after protected repository initialization succeeds.</summary>
public interface ILegacyCredentialCleanup
{
    /// <summary>Removes obsolete plaintext credential files before the engine is started.
    /// Failures must propagate so callers can stop startup without discarding the protected repository.</summary>
    Task CleanupAsync(CancellationToken ct);
}
