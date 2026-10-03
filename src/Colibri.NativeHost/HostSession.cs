using System.Diagnostics.CodeAnalysis;
using System.Text;
using Colibri.Core.Ipc;
using Colibri.Core.Services;

namespace Colibri.NativeHost;

/// <summary>
/// One run of the host: reads messages from the browser until it closes stdin, validates each one and
/// answers with Colibri's response.
/// </summary>
/// <remarks>
/// The extension uses <c>runtime.sendNativeMessage</c>, so the browser starts one host per message, reads
/// the first answer and then closes stdin; reading on until the end also serves a long-lived
/// <c>connectNative</c> port. Everything from the browser is untrusted: only "ping", "config" and "add" are
/// accepted, with the same strict checks as the local pipe (<see cref="IpcProtocol.TryParseRequest"/>), and only
/// the validated request is passed on.
/// </remarks>
internal sealed class HostSession
{
    public const string TooLargeError = "message-too-large";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Func<IpcRequest, CancellationToken, Task<IpcResponse>> _send;
    private readonly HostLog _log;

    public HostSession(Stream input, Stream output, Func<IpcRequest, CancellationToken, Task<IpcResponse>> send, HostLog log)
    {
        _input = input;
        _output = output;
        _send = send;
        _log = log;
    }

    /// <summary>Returns the process exit code: 0 when the browser closed stdin between messages.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        while (true)
        {
            var (status, body) = await NativeMessaging.ReadAsync(_input, NativeMessaging.MaxMessageBytes, ct);
            switch (status)
            {
                case FrameStatus.EndOfStream:
                    return 0;

                case FrameStatus.Truncated:
                    _log.Warning("The browser closed stdin in the middle of a message");
                    return 1;

                case FrameStatus.TooLarge:
                    // The rest of the oversized message cannot be skipped safely; answer and stop.
                    _log.Warning("Rejected a message over the size limit");
                    await ReplyAsync(IpcResponse.Failure(TooLargeError), ct);
                    return 1;
            }

            await ReplyAsync(await HandleAsync(body!, ct), ct);
        }
    }

    /// <summary>Accepts a "ping", "config" or "add" message that passes the local pipe's validation.</summary>
    internal static bool TryParseBrowserMessage(byte[] body, [NotNullWhen(true)] out IpcRequest? request, [NotNullWhen(false)] out string? error)
    {
        request = null;
        string json;
        try
        {
            json = StrictUtf8.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            error = "The message is not valid UTF-8.";
            return false;
        }

        if (!IpcProtocol.TryParseRequest(json, out var parsed, out error))
        {
            return false;
        }

        if (parsed is not (PingRequest or ConfigRequest or AddRequest))
        {
            error = "Unknown request type.";
            return false;
        }

        // The extension never sends extra headers; accepting none keeps what a browser can pass on small.
        if (parsed is AddRequest { Context.Headers.Count: > 0 })
        {
            error = "Headers are not accepted from the browser.";
            return false;
        }

        request = parsed;
        return true;
    }

    private async Task<IpcResponse> HandleAsync(byte[] body, CancellationToken ct)
    {
        if (!TryParseBrowserMessage(body, out var request, out var error))
        {
            _log.Warning($"Rejected a message from the browser: {error}");
            return IpcResponse.Failure(error);
        }

        // Never log cookies or query strings: the URL is redacted.
        _log.Info(request switch
        {
            AddRequest add => $"add {UrlPolicy.Redact(new Uri(add.Url))}",
            PingRequest => "ping",
            _ => "config",
        });
        var response = await _send(request, ct);
        if (!response.Ok)
        {
            _log.Info($"Answered with an error: {response.Error}");
        }

        return response;
    }

    private Task ReplyAsync(IpcResponse response, CancellationToken ct) =>
        NativeMessaging.WriteAsync(_output, Encoding.UTF8.GetBytes(IpcProtocol.SerializeResponse(response)), ct);
}
