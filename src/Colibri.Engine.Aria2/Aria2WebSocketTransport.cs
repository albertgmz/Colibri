using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace Colibri.Engine.Aria2;

/// <summary>
/// <see cref="IAria2Transport"/> over a WebSocket (aria2 serves it at <c>ws://host:port/jsonrpc</c>).
/// </summary>
internal sealed class Aria2WebSocketTransport : IAria2Transport
{
    private readonly WebSocket _socket;

    // A WebSocket allows only one SendAsync at a time (and one ReceiveAsync at a time). Calls come
    // from many threads, so sends are queued through this lock. Receiving happens in one loop only.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>Wraps an already connected socket.</summary>
    internal Aria2WebSocketTransport(WebSocket socket)
    {
        _socket = socket;
    }

    /// <summary>Opens a connection to <paramref name="uri"/>.</summary>
    public static async Task<Aria2WebSocketTransport> ConnectAsync(Uri uri, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(uri, ct);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new Aria2WebSocketTransport(socket);
    }

    public async Task SendAsync(string message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await _sendLock.WaitAsync(ct);
        try
        {
            // The token only limits the wait for our turn. Cancelling a send that has started would
            // abort the whole WebSocket, and a loopback send of one request finishes at once anyway.
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async IAsyncEnumerable<string> ReceiveAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var message = new ArrayBufferWriter<byte>();

        while (true)
        {
            // A message can arrive in several frames (large replies such as tellStopped do).
            // Collect the pieces until EndOfMessage, then decode the whole message at once.
            var result = await _socket.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                yield break;
            }

            message.Write(buffer.AsSpan(0, result.Count));
            if (!result.EndOfMessage)
            {
                continue;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                yield return Encoding.UTF8.GetString(message.WrittenSpan);
            }

            message.ResetWrittenCount();
        }
    }

    public void Dispose()
    {
        // Aborts a pending ReceiveAsync, which ends the receive loop with an exception. The send lock is
        // not disposed: a sender still waiting on it must get the socket's error, not ObjectDisposedException.
        _socket.Dispose();
    }
}
