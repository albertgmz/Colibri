using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Colibri.Core.Ipc;
using Microsoft.Extensions.Logging.Abstractions;
using static Colibri.NativeHost.Tests.NativeMessagingTests;

namespace Colibri.NativeHost.Tests;

public sealed class HostSessionTests : IAsyncDisposable
{
    private static readonly ColibriTimeouts FastTimeouts = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

    private readonly string _pipeName = "colibri-host-test-" + Guid.NewGuid().ToString("N")[..12];
    private readonly List<IpcRequest> _received = [];
    private readonly HostLog _log = new(null);
    private LocalPipeServer? _server;
    private int _starts;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    // ---- Validation ----

    [Theory]
    [InlineData("""{"type":"ping"}""", typeof(PingRequest))]
    [InlineData("""{"type":"config"}""", typeof(ConfigRequest))]
    [InlineData("""{"type":"add","url":"https://example.com/a.zip","fileName":"a.zip","size":10,"cookies":"a=b"}""", typeof(AddRequest))]
    public void Ping_config_and_add_are_accepted(string json, Type expected)
    {
        Assert.True(HostSession.TryParseBrowserMessage(Encoding.UTF8.GetBytes(json), out var request, out var error), error);
        Assert.IsType(expected, request);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"type":"activate","args":[]}""")]
    [InlineData("""{"type":"shutdown"}""")]
    [InlineData("""{"type":1}""")]
    [InlineData("""{"type":"add"}""")]
    [InlineData("""{"type":"add","url":5}""")]
    [InlineData("""{"type":"add","url":"javascript:alert(1)"}""")]
    [InlineData("""{"type":"add","url":"blob:https://example.com/0f6e"}""")]
    [InlineData("""{"type":"add","url":"data:application/zip;base64,AAAA"}""")]
    [InlineData("""{"type":"add","url":"file:///C:/Windows/win.ini"}""")]
    [InlineData("""{"type":"add","url":"https://example.com/a","size":"big"}""")]
    [InlineData("""{"type":"add","url":"https://example.com/a","size":-5}""")]
    [InlineData("""{"type":"add","url":"https://example.com/a","fileName":["a"]}""")]
    [InlineData("""{"type":"add","url":"https://example.com/a","cookies":"a=b\r\nX-Injected: 1"}""")]
    [InlineData("""{"type":"add","url":"https://example.com/a","headers":{"Authorization":"Bearer x"}}""")]
    public void Malformed_or_unsafe_messages_are_rejected(string json)
    {
        Assert.False(HostSession.TryParseBrowserMessage(Encoding.UTF8.GetBytes(json), out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Huge_strings_and_invalid_utf8_are_rejected()
    {
        var cookies = new string('c', IpcProtocol.MaxCookiesLength + 1);
        Assert.False(HostSession.TryParseBrowserMessage(
            Encoding.UTF8.GetBytes($$"""{"type":"add","url":"https://example.com/a","cookies":"{{cookies}}"}"""), out _, out _));

        var url = "https://example.com/" + new string('a', 9000);
        Assert.False(HostSession.TryParseBrowserMessage(Encoding.UTF8.GetBytes($$"""{"type":"add","url":"{{url}}"}"""), out _, out _));

        Assert.False(HostSession.TryParseBrowserMessage([0x7B, 0xC3, 0x28, 0x7D], out _, out var error));
        Assert.Contains("UTF-8", error);
    }

    // ---- Session against a test pipe server ----

    [Fact]
    public async Task Messages_are_forwarded_to_a_running_colibri_and_answered_in_order()
    {
        StartServer();
        var input = Frames(
            """{"type":"ping"}""",
            """{"type":"config"}""",
            """{"type":"add","url":"https://example.com/get?id=1","fileName":"a.zip","cookies":"sid=1","referrer":"https://example.com/","size":42,"unknown":true}""");
        var output = new MemoryStream();

        var exitCode = await Session(input, output).RunAsync(Ct);

        Assert.Equal(0, exitCode);
        var replies = ReadReplies(output);
        Assert.Equal("""{"ok":true}""", replies[0]);
        Assert.Equal("""{"ok":true,"captureExtensions":["zip","iso"],"minSizeKiB":64}""", replies[1]);
        Assert.Equal("""{"ok":true}""", replies[2]);
        Assert.Equal(0, _starts);

        var add = Assert.IsType<AddRequest>(_received[^1]);
        Assert.Equal("https://example.com/get?id=1", add.Url);
        Assert.Equal("sid=1", add.Context.Cookies);
        Assert.Equal(42, add.Context.Size);
    }

    [Fact]
    public async Task An_add_starts_colibri_when_it_is_not_running()
    {
        var output = new MemoryStream();

        var exitCode = await Session(Frames("""{"type":"add","url":"https://example.com/a.zip"}"""), output, startServerOnLaunch: true).RunAsync(Ct);

        Assert.Equal(0, exitCode);
        Assert.Equal(["""{"ok":true}"""], ReadReplies(output));
        Assert.Equal(1, _starts);
        Assert.IsType<AddRequest>(Assert.Single(_received));
    }

    [Fact]
    public async Task A_ping_never_starts_colibri()
    {
        var output = new MemoryStream();

        await Session(Frames("""{"type":"ping"}"""), output, startServerOnLaunch: true).RunAsync(Ct);

        Assert.Equal(["""{"ok":false,"error":"app-not-running"}"""], ReadReplies(output));
        Assert.Equal(0, _starts);
    }

    [Fact]
    public async Task When_colibri_cannot_be_started_the_browser_gets_an_error()
    {
        var output = new MemoryStream();
        var client = new ColibriClient(_pipeName, () => { _starts++; return false; }, FastTimeouts, _log);

        await new HostSession(Frames("""{"type":"add","url":"https://example.com/a.zip"}"""), output, client.SendAsync, _log).RunAsync(Ct);

        var reply = JsonDocument.Parse(Assert.Single(ReadReplies(output))).RootElement;
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(1, _starts);
    }

    [Fact]
    public async Task An_invalid_message_is_answered_with_an_error_and_the_session_goes_on()
    {
        StartServer();
        var output = new MemoryStream();

        var exitCode = await Session(Frames("""{"type":"add","url":"javascript:void(0)"}""", """{"type":"ping"}"""), output).RunAsync(Ct);

        Assert.Equal(0, exitCode);
        var replies = ReadReplies(output);
        Assert.StartsWith("""{"ok":false""", replies[0]);
        Assert.Equal("""{"ok":true}""", replies[1]);
        Assert.IsType<PingRequest>(Assert.Single(_received)); // The invalid add never reached Colibri.
    }

    [Fact]
    public async Task An_oversized_message_is_answered_and_ends_the_session()
    {
        StartServer();
        var oversized = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(oversized, NativeMessaging.MaxMessageBytes + 1);
        var output = new MemoryStream();

        var exitCode = await Session(new MemoryStream(oversized.Concat(Frame("""{"type":"ping"}""")).ToArray()), output).RunAsync(Ct);

        Assert.Equal(1, exitCode);
        Assert.Equal(["""{"ok":false,"error":"message-too-large"}"""], ReadReplies(output));
        Assert.Empty(_received);
    }

    [Fact]
    public async Task A_truncated_message_ends_the_session_without_an_answer()
    {
        var output = new MemoryStream();

        var exitCode = await Session(new MemoryStream([10, 0, 0, 0, (byte)'{']), output).RunAsync(Ct);

        Assert.Equal(1, exitCode);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task A_message_arriving_in_pieces_is_handled()
    {
        StartServer();
        var output = new MemoryStream();

        await Session(new OneByteAtATimeStream(Frame("""{"type":"ping"}""")), output).RunAsync(Ct);

        Assert.Equal(["""{"ok":true}"""], ReadReplies(output));
    }

    private HostSession Session(Stream input, Stream output, bool startServerOnLaunch = false)
    {
        var client = new ColibriClient(
            _pipeName,
            () =>
            {
                _starts++;
                if (startServerOnLaunch)
                {
                    StartServer();
                }

                return true;
            },
            FastTimeouts,
            _log);
        return new HostSession(input, output, client.SendAsync, _log);
    }

    /// <summary>Plays the running Colibri: answers "config" with fixed rules and everything else with ok.</summary>
    private void StartServer()
    {
        _server = new LocalPipeServer(_pipeName, (request, _) =>
        {
            lock (_received)
            {
                _received.Add(request);
            }

            return Task.FromResult(request is ConfigRequest
                ? new IpcResponse(true, Config: new CaptureConfig(["zip", "iso"], 64))
                : IpcResponse.Success);
        }, NullLogger.Instance);
        _server.Start();
    }

    private static MemoryStream Frames(params string[] messages) => new(messages.SelectMany(Frame).ToArray());

    private static List<string> ReadReplies(MemoryStream output)
    {
        var data = output.ToArray();
        var replies = new List<string>();
        for (var i = 0; i < data.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i));
            replies.Add(Encoding.UTF8.GetString(data, i + 4, length));
            i += 4 + length;
        }

        return replies;
    }
}
