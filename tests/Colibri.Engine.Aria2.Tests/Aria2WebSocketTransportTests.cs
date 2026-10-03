using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace Colibri.Engine.Aria2.Tests;

/// <summary>
/// Runs the transport against a real WebSocket peer over a loopback TCP connection.
/// </summary>
public class Aria2WebSocketTransportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(WebSocket Server, Aria2WebSocketTransport Transport, IDisposable Resources)> ConnectPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var accept = listener.AcceptTcpClientAsync(Ct);
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, Ct);
        var server = await accept;
        listener.Stop();

        // CreateFromStream skips the HTTP handshake and speaks WebSocket framing directly.
        var serverSocket = WebSocket.CreateFromStream(server.GetStream(), isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
        var clientSocket = WebSocket.CreateFromStream(client.GetStream(), isServer: false, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
        return (serverSocket, new Aria2WebSocketTransport(clientSocket), new Resources(client, server));
    }

    [Fact]
    public async Task A_message_sent_in_several_frames_is_received_whole()
    {
        var (server, transport, resources) = await ConnectPairAsync();
        using var _ = resources;
        await using var messages = transport.ReceiveAllAsync(Ct).GetAsyncEnumerator(Ct);

        var text = """{"jsonrpc":"2.0","id":"1","result":"ñandú"}""";
        var bytes = Encoding.UTF8.GetBytes(text);

        // Split inside the two-byte "ñ" so decoding per frame would corrupt it.
        var split = Array.IndexOf(bytes, (byte)0xC3) + 1;
        await server.SendAsync(bytes.AsMemory(0, split), WebSocketMessageType.Text, endOfMessage: false, Ct);
        await server.SendAsync(bytes.AsMemory(split), WebSocketMessageType.Text, endOfMessage: true, Ct);
        await server.SendAsync("""{"second":true}"""u8.ToArray(), WebSocketMessageType.Text, endOfMessage: true, Ct);

        Assert.True(await messages.MoveNextAsync());
        Assert.Equal(text, messages.Current);
        Assert.True(await messages.MoveNextAsync());
        Assert.Equal("""{"second":true}""", messages.Current);
    }

    [Fact]
    public async Task A_message_larger_than_the_receive_buffer_is_received_whole()
    {
        var (server, transport, resources) = await ConnectPairAsync();
        using var _ = resources;
        await using var messages = transport.ReceiveAllAsync(Ct).GetAsyncEnumerator(Ct);

        var text = "\"" + new string('x', 100_000) + "\"";
        await server.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, Ct);

        Assert.True(await messages.MoveNextAsync());
        Assert.Equal(text, messages.Current);
    }

    [Fact]
    public async Task Sent_messages_arrive_as_text()
    {
        var (server, transport, resources) = await ConnectPairAsync();
        using var _ = resources;

        await transport.SendAsync("""{"id":"1"}""", Ct);

        var buffer = new byte[1024];
        var result = await server.ReceiveAsync(buffer, Ct);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.True(result.EndOfMessage);
        Assert.Equal("""{"id":"1"}""", Encoding.UTF8.GetString(buffer, 0, result.Count));
    }

    [Fact]
    public async Task Receiving_ends_when_the_peer_closes()
    {
        var (server, transport, resources) = await ConnectPairAsync();
        using var _ = resources;
        await using var messages = transport.ReceiveAllAsync(Ct).GetAsyncEnumerator(Ct);

        await server.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, Ct);

        Assert.False(await messages.MoveNextAsync());
    }

    private sealed class Resources(TcpClient client, TcpClient server) : IDisposable
    {
        public void Dispose()
        {
            client.Dispose();
            server.Dispose();
        }
    }
}
