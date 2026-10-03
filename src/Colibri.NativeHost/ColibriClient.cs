using System.IO.Pipes;
using Colibri.Core.Ipc;
using Colibri.Core.Platform;

namespace Colibri.NativeHost;

/// <summary>How long the host waits for Colibri.</summary>
/// <param name="Connect">To find a running Colibri.</param>
/// <param name="Start">To find Colibri after starting it.</param>
/// <param name="Response">For Colibri's answer once connected (Colibri answers an "add" when its window is shown).</param>
internal sealed record ColibriTimeouts(TimeSpan Connect, TimeSpan Start, TimeSpan Response)
{
    /// <summary>18 s at most, within the extension's 20 s: past that the browser keeps its download, and a late "ok" would make a second copy.</summary>
    public static ColibriTimeouts Default { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(5));
}

/// <summary>
/// Forwards a validated request to the running Colibri over the local pipe. For "add" and "config" a
/// Colibri that is not running is started first; "ping" only reports it.
/// </summary>
internal sealed class ColibriClient
{
    /// <summary>The answer to "ping" when Colibri is not running; the extension shows it as such.</summary>
    public const string NotRunningError = "app-not-running";

    private readonly string _pipeName;
    private readonly Func<bool> _startApp;
    private readonly IForegroundHandoff _foreground;
    private readonly ColibriTimeouts _timeouts;
    private readonly HostLog _log;

    public ColibriClient(string pipeName, Func<bool> startApp, IForegroundHandoff foreground, ColibriTimeouts timeouts, HostLog log)
    {
        _pipeName = pipeName;
        _startApp = startApp;
        _foreground = foreground;
        _timeouts = timeouts;
        _log = log;
    }

    public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct)
    {
        var pipe = await TryConnectAsync(_timeouts.Connect, ct);
        if (pipe is null)
        {
            // Startup handshakes only discover a running app. Intentional add/open requests launch it.
            if (request is HelloRequest)
                return new IpcResponse(false, Error: NotRunningError, ProtocolVersion: BrowserProtocol.Version,
                    Capabilities: BrowserProtocol.Capabilities);
            if (request is PingRequest or CaptureStatusRequest or CaptureCancelRequest)
            {
                return IpcResponse.Failure(NotRunningError);
            }

            _log.Info("Colibri is not running; starting it");
            if (!_startApp())
            {
                return IpcResponse.Failure("Colibri could not be started.");
            }

            pipe = await TryConnectAsync(_timeouts.Start, ct);
            if (pipe is null)
            {
                _log.Warning("Colibri did not answer after it was started");
                return IpcResponse.Failure("Colibri did not answer after it was started.");
            }
        }

        await using (pipe)
        {
            // An "add" opens Colibri's Add URL window, which must come up in front of the browser.
            if (request is AddRequest or BulkAddRequest or OpenRequest && !_foreground.AllowAppToTakeForeground(pipe))
            {
                _log.Info("Could not let Colibri come to the front; its window may open behind the browser");
            }

            try
            {
                return await LocalPipeClient.ExchangeAsync(pipe, request, _timeouts.Response, ct);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                _log.Warning($"Colibri did not answer: {ex.Message}");
                return IpcResponse.Failure("Colibri did not answer.");
            }
        }
    }

    private async Task<NamedPipeClientStream?> TryConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            return await LocalPipeClient.ConnectAsync(_pipeName, timeout, ct);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // For example a pipe of that name owned by another user: not our Colibri.
            _log.Warning($"Could not connect to Colibri: {ex.Message}");
            return null;
        }
    }
}
