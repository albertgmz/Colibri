using Colibri.Core.Models;

namespace Colibri.Core.Services;

/// <summary>Session observations only: old bytes and time spent queued or paused never enter the average.</summary>
public sealed class DownloadTelemetry
{
    public const int MaxSamples = 300;
    public const int MaxEvents = 200;
    private readonly Queue<DownloadSpeedSample> _samples = new();
    private readonly Queue<DownloadLogEntry> _events = new();
    private DownloadItem? _previous;
    private DateTimeOffset _last;
    private long _bytes;
    private double _activeSeconds;

    public double? AverageBytesPerSecond => _activeSeconds > 0 ? _bytes / _activeSeconds : null;
    public IReadOnlyList<DownloadSpeedSample> SpeedHistory => _samples.ToArray();
    public IReadOnlyList<DownloadLogEntry> Events => _events.ToArray();

    public void Observe(DownloadItem item, DateTimeOffset now)
    {
        if (_previous is { } previous)
        {
            if (previous.State == DownloadState.Active && now > _last)
            {
                _activeSeconds += (now - _last).TotalSeconds;
                _bytes += Math.Max(0, item.CompletedBytes - previous.CompletedBytes);
            }
            if (previous.State != item.State)
                AddEvent(now, item.State switch
                {
                    DownloadState.Active => DownloadLogKind.Started,
                    DownloadState.Paused => DownloadLogKind.Paused,
                    DownloadState.Completed => DownloadLogKind.Completed,
                    DownloadState.Failed => DownloadLogKind.Failed,
                    _ when previous.State == DownloadState.Failed => DownloadLogKind.Retried,
                    _ => DownloadLogKind.Queued,
                }, item.State == DownloadState.Failed ? item.ErrorMessage : null);
            if (item.FinalUrl is { } final && final != previous.FinalUrl && final != item.Url)
                AddEvent(now, DownloadLogKind.Redirected, Uri.TryCreate(final, UriKind.Absolute, out var uri) ? UrlPolicy.Redact(uri) : null);
        }
        else AddEvent(now, DownloadLogKind.Observed);

        // Engine snapshots and their published row changes can arrive milliseconds apart.
        // Keep one speed sample per second so the count bound retains five minutes;
        // state events and average measurements above still observe every change.
        if (_samples.Count == 0 || now - _samples.Last().Timestamp >= TimeSpan.FromSeconds(1))
            _samples.Enqueue(new(now, item.State == DownloadState.Active ? item.DownloadSpeed : 0));
        while (_samples.Count > MaxSamples || (_samples.Count > 0 && now - _samples.Peek().Timestamp > TimeSpan.FromMinutes(5)))
            _samples.Dequeue();
        _previous = item.Clone();
        _last = now;
    }

    private void AddEvent(DateTimeOffset now, DownloadLogKind kind, string? message = null)
    {
        _events.Enqueue(new(now, kind, message));
        while (_events.Count > MaxEvents) _events.Dequeue();
    }
}
