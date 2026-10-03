namespace Colibri.Core.Models;

public enum DownloadLogKind { Observed, Started, Queued, Paused, Retried, Completed, Failed, Redirected }
public sealed record DownloadLogEntry(DateTimeOffset Timestamp, DownloadLogKind Kind, string? Message = null);
public sealed record DownloadSpeedSample(DateTimeOffset Timestamp, long BytesPerSecond);
public sealed record DownloadServer(int FileIndex, string Host, string? CurrentUrl, long BytesPerSecond);
public sealed record DownloadTransferOptions(long SpeedLimitBytesPerSecond, int ConnectionsPerServer);
public sealed record EngineDownloadDetails(IReadOnlyList<DownloadServer> Servers, DownloadTransferOptions Options);
public sealed record DownloadDetails(
    DownloadItem Item, IReadOnlyList<DownloadSpeedSample> SpeedHistory,
    IReadOnlyList<DownloadLogEntry> Events, double? AverageBytesPerSecond,
    IReadOnlyList<DownloadServer> Servers, DownloadTransferOptions? Options,
    string? EngineDetailsError);
