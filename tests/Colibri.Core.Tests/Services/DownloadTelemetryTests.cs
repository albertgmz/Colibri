using Colibri.Core.Models;
using Colibri.Core.Services;
using Colibri.Core.Tests.Fakes;

namespace Colibri.Core.Tests.Services;

public class DownloadTelemetryTests
{
    [Fact]
    public void Duplicate_observations_between_poll_ticks_preserve_five_minutes_of_speed_history()
    {
        var time = new ManualTimeProvider();
        var history = new DownloadTelemetry();
        var item = new DownloadItem { State = DownloadState.Active, DownloadSpeed = 100 };
        for (var tick = 0; tick < 600; tick++)
        {
            item.CompletedBytes += 100;
            history.Observe(item, time.GetUtcNow());
            // Publishing a changed row observes it again after the engine snapshot.
            time.Advance(TimeSpan.FromMilliseconds(10));
            history.Observe(item, time.GetUtcNow());
            time.Advance(TimeSpan.FromMilliseconds(990));
        }

        var samples = history.SpeedHistory;
        Assert.Equal(DownloadTelemetry.MaxSamples, samples.Count);
        Assert.Equal(TimeSpan.FromSeconds(299), samples[^1].Timestamp - samples[0].Timestamp);
        Assert.All(samples.Zip(samples.Skip(1)), pair =>
            Assert.True(pair.Second.Timestamp - pair.First.Timestamp >= TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Speed_sample_throttling_preserves_rapid_state_events_and_average_observations()
    {
        var time = new ManualTimeProvider();
        var history = new DownloadTelemetry();
        var item = new DownloadItem { State = DownloadState.Active, DownloadSpeed = 1000 };
        history.Observe(item, time.GetUtcNow());
        time.Advance(TimeSpan.FromMilliseconds(100));
        item.CompletedBytes = 100;
        item.State = DownloadState.Paused;
        history.Observe(item, time.GetUtcNow());

        Assert.Single(history.SpeedHistory);
        Assert.Contains(history.Events, entry => entry.Kind == DownloadLogKind.Paused);
        Assert.Equal(1000, history.AverageBytesPerSecond);
    }

    [Fact]
    public void Redirect_events_exclude_url_credentials_query_and_fragment()
    {
        var history = new DownloadTelemetry();
        var item = new DownloadItem { Url = "https://example.com/file" };
        history.Observe(item, DateTimeOffset.UnixEpoch);
        item.FinalUrl = "https://user:password@cdn.example.com/file?token=private#secret";
        history.Observe(item, DateTimeOffset.UnixEpoch.AddSeconds(1));
        Assert.Equal("https://cdn.example.com/file", Assert.Single(history.Events, entry => entry.Kind == DownloadLogKind.Redirected).Message);
    }
    [Fact]
    public void Average_excludes_queue_pause_and_bytes_from_an_earlier_session()
    {
        var history = new DownloadTelemetry();
        var item = new DownloadItem { CompletedBytes = 1000 };
        var now = DateTimeOffset.UnixEpoch;
        history.Observe(item, now);
        item.State = DownloadState.Active;
        history.Observe(item, now.AddMinutes(2));
        item.CompletedBytes = 2000;
        item.State = DownloadState.Paused;
        history.Observe(item, now.AddMinutes(2).AddSeconds(10));
        item.State = DownloadState.Active;
        history.Observe(item, now.AddMinutes(6));
        item.CompletedBytes = 3000;
        history.Observe(item, now.AddMinutes(6).AddSeconds(10));
        Assert.Equal(100, history.AverageBytesPerSecond);
        Assert.Contains(history.Events, e => e.Kind == DownloadLogKind.Paused);
    }

    [Fact]
    public void Histories_are_bounded_and_old_speed_samples_expire()
    {
        var history = new DownloadTelemetry();
        var item = new DownloadItem();
        for (var i = 0; i < 1000; i++)
        {
            item.State = i % 2 == 0 ? DownloadState.Active : DownloadState.Paused;
            history.Observe(item, DateTimeOffset.UnixEpoch.AddSeconds(i));
        }
        Assert.Equal(DownloadTelemetry.MaxEvents, history.Events.Count);
        Assert.Equal(DownloadTelemetry.MaxSamples, history.SpeedHistory.Count);
        history.Observe(item, DateTimeOffset.UnixEpoch.AddHours(1));
        Assert.Single(history.SpeedHistory);
    }

    [Theory]
    [InlineData("ff", 5, 5)]
    [InlineData("80", 8, 1)]
    [InlineData("01", 8, 1)]
    [InlineData("00", 8, 0)]
    [InlineData("F0", 8, 4)]
    [InlineData("z0", 8, null)]
    [InlineData("f", 8, null)]
    [InlineData(null, 8, null)]
    public void Piece_counts_use_actual_bits_and_ignore_padding(string? bits, int count, int? expected) =>
        Assert.Equal(expected, PieceMap.CompletedCount(bits, count));
}
