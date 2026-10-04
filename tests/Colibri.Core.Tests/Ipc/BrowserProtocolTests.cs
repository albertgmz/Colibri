using Colibri.Core.Ipc;
using Colibri.Core.Services;

namespace Colibri.Core.Tests.Ipc;

public class BrowserProtocolTests
{
    [Fact]
    public void Hello_exchanges_supported_version_and_capabilities()
    {
        Assert.True(IpcProtocol.TryParseRequest("""{"type":"hello","protocolVersion":2,"extensionVersion":"2.0","capabilities":["capture-confirmation"]}""", out var request, out _));
        Assert.IsType<HelloRequest>(request);
        var reply = new IpcResponse(true, ProtocolVersion: 2, AppVersion: "2.0", Capabilities: BrowserProtocol.Capabilities);
        var roundtrip = IpcProtocol.ParseResponse(IpcProtocol.SerializeResponse(reply));
        Assert.Equal(2, roundtrip.ProtocolVersion);
        Assert.Contains("capture-confirmation", roundtrip.Capabilities!);
    }

    [Theory]
    [InlineData("""{"type":"hello","protocolVersion":3}""")]
    [InlineData("""{"type":"add","protocolVersion":1,"url":"https://example.com/a"}""")]
    [InlineData("""{"type":"add","url":"https://example.com/a","requestMethod":"POST"}""")]
    [InlineData("""{"type":"bulk-add","links":[7]}""")]
    [InlineData("""{"type":"settings-update","patch":{"excludedSites":["https://example.com/"]}}""")]
    [InlineData("""{"type":"capture-status","captureId":"wrong"}""")]
    public void Unsupported_or_unsafe_context_is_rejected(string json)
    {
        Assert.False(IpcProtocol.TryParseRequest(json, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Context_roundtrip_keeps_real_response_and_redirect_metadata()
    {
        Assert.True(IpcProtocol.TryParseRequest("""{"type":"add","url":"https://example.com/a","finalUrl":"https://cdn.example.com/a","requestMethod":"GET","redirects":["https://cdn.example.com/a"],"responseStatus":200,"contentDisposition":"attachment; filename=a.zip","headers":{"Authorization":"Bearer test","Range":"bytes=0-"}}""", out var request, out _));
        Assert.True(IpcProtocol.TryParseRequest(IpcProtocol.SerializeRequest(request!), out var replay, out _));
        var add = Assert.IsType<AddRequest>(replay);
        Assert.Equal(200, add.Context.ResponseStatus);
        Assert.Single(add.Context.Redirects);
        Assert.True(add.Context.Headers.ContainsKey("Authorization"));
        Assert.False(add.Context.Headers.ContainsKey("Range"));
    }

    [Fact]
    public void Browser_fallback_invalidates_pending_confirmation()
    {
        using var offer = new CaptureSession(TimeSpan.FromMinutes(5));
        Assert.Equal("pending", offer.State);
        offer.Reject("browser");
        Assert.True(offer.Token.IsCancellationRequested);
        Assert.False(offer.Accept());
        Assert.Equal("browser", offer.State);
    }

    [Fact]
    public void Accepted_capture_cannot_be_changed_by_late_cancel()
    {
        using var offer = new CaptureSession(TimeSpan.FromMinutes(5));
        Assert.True(offer.Accept());
        offer.Reject("browser");
        Assert.Equal("accepted", offer.State);
    }

    [Fact]
    public async Task Rejection_waits_for_transfer_cleanup_before_releasing_browser()
    {
        using var offer = new CaptureSession(TimeSpan.FromMinutes(5));
        var operation = Assert.IsType<CaptureSession.Operation>(offer.TryBeginOperation());
        Assert.Null(offer.TryBeginOperation());
        offer.Reject("browser");
        Assert.Equal("pending", offer.State);
        Assert.True(offer.Token.IsCancellationRequested);
        Assert.False(offer.Accept());
        var settled = offer.WaitForOperationAsync(TestContext.Current.CancellationToken);
        Assert.False(settled.IsCompleted);
        operation.Dispose();
        await settled;
        Assert.Equal("browser", offer.State);
    }

    [Fact]
    public void Failed_cleanup_before_later_rejection_does_not_release_browser()
    {
        using var offer = new CaptureSession(TimeSpan.FromMinutes(5));
        var operation = Assert.IsType<CaptureSession.Operation>(offer.TryBeginOperation());
        operation.CleanupFailed();
        operation.Dispose();
        offer.Reject("browser");
        Assert.Equal("pending", offer.State);
        Assert.Null(offer.TryBeginOperation());
    }

    [Fact]
    public async Task Failed_cleanup_keeps_browser_offer_pending_for_manual_attention()
    {
        using var offer = new CaptureSession(TimeSpan.FromMinutes(5));
        var operation = Assert.IsType<CaptureSession.Operation>(offer.TryBeginOperation());
        offer.Reject("browser");
        operation.CleanupFailed();
        operation.Dispose();
        await offer.WaitForOperationAsync(TestContext.Current.CancellationToken);
        Assert.Equal("pending", offer.State);
        Assert.False(offer.Accept());
        Assert.Null(offer.TryBeginOperation());
    }

    [Fact]
    public void Expired_offer_returns_to_browser()
    {
        var time = new ControlledTimeProvider();
        using var offer = new CaptureSession(TimeSpan.FromMilliseconds(20), time);
        time.Advance(TimeSpan.FromMilliseconds(19));
        Assert.Equal("pending", offer.State);
        Assert.False(offer.Token.IsCancellationRequested);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("browser", offer.State);
        Assert.True(offer.Token.IsCancellationRequested);
        Assert.False(offer.Accept());
    }

    [Fact]
    public void Accepted_capture_disables_expiry_timer()
    {
        var time = new ControlledTimeProvider();
        using var offer = new CaptureSession(TimeSpan.FromSeconds(1), time);
        Assert.True(offer.Accept());
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("accepted", offer.State);
        Assert.False(offer.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Expiry_waits_for_transfer_cleanup_before_releasing_browser()
    {
        var time = new ControlledTimeProvider();
        using var offer = new CaptureSession(TimeSpan.FromSeconds(1), time);
        var operation = Assert.IsType<CaptureSession.Operation>(offer.TryBeginOperation());
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(offer.Token.IsCancellationRequested);
        Assert.Equal("pending", offer.State);
        Assert.False(offer.Accept());
        Assert.Null(offer.TryBeginOperation());
        var settled = offer.WaitForOperationAsync(TestContext.Current.CancellationToken);
        Assert.False(settled.IsCompleted);
        operation.Dispose();
        await settled;
        Assert.Equal("browser", offer.State);
    }

    [Fact]
    public void Expiry_keeps_failed_cleanup_pending_for_manual_attention()
    {
        var time = new ControlledTimeProvider();
        using var offer = new CaptureSession(TimeSpan.FromSeconds(1), time);
        var operation = Assert.IsType<CaptureSession.Operation>(offer.TryBeginOperation());
        time.Advance(TimeSpan.FromSeconds(1));
        operation.CleanupFailed();
        operation.Dispose();
        Assert.Equal("pending", offer.State);
        Assert.True(offer.Token.IsCancellationRequested);
        Assert.False(offer.Accept());
        Assert.Null(offer.TryBeginOperation());
    }

    /// <summary>Executes scheduled callbacks synchronously when time advances; no thread-pool delays.</summary>
    private sealed class ControlledTimeProvider : TimeProvider
    {
        private TimeSpan _elapsed;
        private readonly List<ControlledTimer> _timers = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ControlledTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            _elapsed += elapsed;
            foreach (var timer in _timers.ToArray()) timer.FireIfDue();
        }
        private sealed class ControlledTimer(ControlledTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            private TimeSpan? _due;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                // CaptureSession's CTS is a one-shot timer; fail loudly if its contract changes.
                if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Only one-shot timers are supported.");
                _due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._elapsed + dueTime;
                return true;
            }
            public void FireIfDue()
            {
                if (_disposed || _due is not { } due || due > owner._elapsed) return;
                _due = null;
                callback(state);
            }
            public void Dispose() { _disposed = true; _due = null; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
