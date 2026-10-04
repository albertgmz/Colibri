using Colibri.Core.Engine;

namespace Colibri.Core.Torrents;

public sealed record TorrentFile(int Index, string RelativePath, long Length);
public sealed record TorrentMetadata(string InfoHash, string Name, IReadOnlyList<TorrentFile> Files,
    long TotalLength, bool IsPrivate);
public sealed record TorrentMetadataResult(TorrentMetadata Metadata, byte[] Metainfo);
public sealed record TorrentDownload(byte[] Metainfo, IReadOnlyList<int> SelectedFileIndices, TorrentSeedOptions SeedOptions);
public sealed record TorrentSeedOptions(bool Enabled = false, double Ratio = 1, int TimeLimitMinutes = 60,
    long UploadLimitBytesPerSecond = 65536)
{
    public void Validate()
    {
        if (!double.IsFinite(Ratio) || Ratio is <= 0 or > 1000 || TimeLimitMinutes is < 1 or > 10080
            || UploadLimitBytesPerSecond is < 1 or > 1_000_000_000)
            throw new ArgumentException("Invalid torrent seeding limits.");
    }
}

public interface ITorrentEngine : IDownloadEngine
{
    /// <summary>Fetches metadata only, then removes the temporary job before returning. No content promotion.</summary>
    Task<TorrentMetadataResult> PreviewMagnetAsync(string magnet, CancellationToken ct);
    Task StopSeedingAsync(string handle, CancellationToken ct);
}
