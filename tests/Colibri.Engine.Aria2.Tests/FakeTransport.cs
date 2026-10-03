using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Colibri.Engine.Aria2.Tests;

/// <summary>
/// In-memory stand-in for aria2: records what the client sends and lets a test push messages back,
/// either by hand or through <see cref="Responder"/>.
/// </summary>
internal sealed class FakeTransport : IAria2Transport
{
    private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
    private readonly Channel<JsonObject> _sent = Channel.CreateUnbounded<JsonObject>();

    /// <summary>
    /// Answers each request automatically. Return <see cref="Result"/> or <see cref="Error"/>, or null to
    /// send no answer. The jsonrpc and id fields are added here.
    /// </summary>
    public Func<JsonObject, JsonObject?>? Responder { get; set; }

    public ConcurrentQueue<JsonObject> Sent { get; } = new();

    public bool Disposed { get; private set; }

    public static JsonObject Result(JsonNode? result) => new() { ["result"] = result };

    public static JsonObject Error(int code, string message) =>
        new() { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    /// <summary>When set, every send throws this (like a socket that has died).</summary>
    public Exception? SendFailure { get; set; }

    public Task SendAsync(string message, CancellationToken ct)
    {
        if (SendFailure is not null)
        {
            throw SendFailure;
        }

        var request = JsonNode.Parse(message)!.AsObject();
        Sent.Enqueue(request);
        _sent.Writer.TryWrite(request);

        if (Responder?.Invoke(request) is { } reply)
        {
            reply["jsonrpc"] = "2.0";
            reply["id"] = request["id"]!.DeepClone();
            Push(reply.ToJsonString());
        }

        return Task.CompletedTask;
    }

    public IAsyncEnumerable<string> ReceiveAllAsync(CancellationToken ct) => _incoming.Reader.ReadAllAsync(ct);

    /// <summary>Waits for the next request the client sends.</summary>
    public async Task<JsonObject> NextRequestAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await _sent.Reader.ReadAsync(timeout.Token);
    }

    /// <summary>Delivers a message to the client as if aria2 had sent it.</summary>
    public void Push(string message) => _incoming.Writer.TryWrite(message);

    /// <summary>Simulates the socket failing.</summary>
    public void Drop() => _incoming.Writer.TryComplete(new WebSocketException("Connection reset"));

    public void Dispose()
    {
        Disposed = true;
        _incoming.Writer.TryComplete();
    }
}
