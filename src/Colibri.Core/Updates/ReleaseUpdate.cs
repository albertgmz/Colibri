namespace Colibri.Core.Updates;

public enum UpdatePackageKind { None, Portable, Installer }

public sealed record ReleasePackage(string Name, Uri Url, long Size, string Sha256);
public sealed record ReleaseUpdate(string Version, ReleasePackage Portable, ReleasePackage Installer);
public sealed record VerifiedUpdatePackage(string Path, ReleasePackage Source);

/// <summary>Public release metadata and verified, explicitly requested package downloads.</summary>
public interface IReleaseUpdateService
{
    string CurrentVersion { get; }
    UpdatePackageKind PackageKind { get; }
    Task<ReleaseUpdate?> CheckAsync(CancellationToken cancellationToken);
    Task<VerifiedUpdatePackage> DownloadAsync(ReleaseUpdate update, CancellationToken cancellationToken);
    Task PrepareInstallAfterExitAsync(VerifiedUpdatePackage package, CancellationToken cancellationToken);
}

public sealed class UpdateFeedUnavailableException(string message) : Exception(message);
