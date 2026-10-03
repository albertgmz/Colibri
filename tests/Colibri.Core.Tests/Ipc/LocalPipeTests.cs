using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Colibri.Core.Ipc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Colibri.Core.Tests.Ipc;

/// <summary>Over a real named pipe (a Unix domain socket on Linux and macOS) with a unique name per test.</summary>
public sealed class LocalPipeTests : IAsyncDisposable
{
    private readonly string _pipeName = "colibri-test-" + Guid.NewGuid().ToString("N")[..12];
    private readonly List<IpcRequest> _received = [];
    private LocalPipeServer? _server;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    private void StartServer(IpcResponse? answer = null)
    {
        _server = new LocalPipeServer(_pipeName, (request, _) =>
        {
            lock (_received)
            {
                _received.Add(request);
            }

            return Task.FromResult(answer ?? IpcResponse.Success);
        }, NullLogger.Instance);
        _server.Start();
    }

    /// <summary>Sends raw bytes and reads the raw answer, like a client that does not use LocalPipeClient.</summary>
    private async Task<string?> SendRawAsync(byte[] data)
    {
        await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000, Ct);

        // Written in the background: the server may stop reading (oversized request) before all of it is sent.
        var write = Task.Run(async () =>
        {
            try
            {
                await pipe.WriteAsync(data, Ct);
                await pipe.FlushAsync(Ct);
            }
            catch (IOException)
            {
                // The server hung up early.
            }
        }, Ct);

        var (line, _) = await IpcProtocol.ReadLineAsync(pipe, IpcProtocol.MaxLineBytes, Ct);
        await write;
        return line;
    }

    [Fact]
    public async Task Request_and_response_round_trip()
    {
        StartServer();

        var response = await LocalPipeClient.SendAsync(_pipeName, new ActivateRequest(["--minimized"]), TimeSpan.FromSeconds(5), Ct);

        Assert.True(response.Ok);
        var request = Assert.IsType<ActivateRequest>(Assert.Single(_received));
        Assert.Equal(["--minimized"], request.Args);
    }

    [Fact]
    public async Task Several_requests_in_a_row_are_served()
    {
        StartServer();

        for (var i = 0; i < 3; i++)
        {
            Assert.True((await LocalPipeClient.SendAsync(_pipeName, new ActivateRequest([]), TimeSpan.FromSeconds(5), Ct)).Ok);
        }

        Assert.Equal(3, _received.Count);
    }

    [Fact]
    public async Task The_handlers_failure_answer_reaches_the_client()
    {
        StartServer(IpcResponse.Failure("busy"));

        var response = await LocalPipeClient.SendAsync(_pipeName, new ActivateRequest([]), TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(IpcResponse.Failure("busy"), response);
    }

    [Fact]
    public async Task Malformed_json_gets_an_error_response_and_does_not_reach_the_handler()
    {
        StartServer();

        var answer = await SendRawAsync("{not json\n"u8.ToArray());

        var response = IpcProtocol.ParseResponse(answer!);
        Assert.False(response.Ok);
        Assert.NotNull(response.Error);
        Assert.Empty(_received);
    }

    [Fact]
    public async Task Unknown_request_type_is_rejected()
    {
        StartServer();

        var answer = await SendRawAsync("""{"type":"format-disk"}"""u8.ToArray().Append((byte)'\n').ToArray());

        Assert.False(IpcProtocol.ParseResponse(answer!).Ok);
        Assert.Empty(_received);
    }

    [Fact]
    public async Task Oversized_line_is_rejected()
    {
        StartServer();
        var huge = Encoding.UTF8.GetBytes("{\"type\":\"activate\",\"args\":[\"" + new string('a', IpcProtocol.MaxLineBytes + 10) + "\"]}\n");

        var answer = await SendRawAsync(huge);

        var response = IpcProtocol.ParseResponse(answer!);
        Assert.False(response.Ok);
        Assert.Contains("too large", response.Error);
        Assert.Empty(_received);
    }

    [Fact]
    public async Task Client_times_out_when_no_server_listens()
    {
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            LocalPipeClient.SendAsync(_pipeName, new ActivateRequest([]), TimeSpan.FromMilliseconds(500), Ct));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Took {watch.Elapsed}");
    }

    [Fact]
    public async Task Client_waits_for_a_server_that_is_still_starting()
    {
        var send = LocalPipeClient.SendAsync(_pipeName, new ActivateRequest([]), TimeSpan.FromSeconds(5), Ct);
        await Task.Delay(300, Ct);
        StartServer();

        Assert.True((await send).Ok);
    }
}
