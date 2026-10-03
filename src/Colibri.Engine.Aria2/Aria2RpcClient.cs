using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Colibri.Core.Engine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Colibri.Engine.Aria2;

/// <summary>
/// JSON-RPC 2.0 client for aria2 over one <see cref="IAria2Transport"/> connection.
/// Safe to call from many threads. Answers are matched to calls by their "id".
/// </summary>
/// <remarks>
/// aria2 sends every number as a JSON string (for example <c>"completedLength":"33423360"</c>),
/// so results are returned as <see cref="JsonNode"/> and parsed by <see cref="Aria2Status"/>.
/// </remarks>
internal sealed class Aria2RpcClient : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    // aria2 notification method -> event kind. Notifications carry params: [{"gid":"..."}] and no id.
    private static readonly Dictionary<string, EngineDownloadEventKind> NotificationKinds = new()
    {
        ["aria2.onDownloadStart"] = EngineDownloadEventKind.Started,
        ["aria2.onDownloadPause"] = EngineDownloadEventKind.Paused,
        ["aria2.onDownloadStop"] = EngineDownloadEventKind.Stopped,
        ["aria2.onDownloadComplete"] = EngineDownloadEventKind.Completed,
        ["aria2.onDownloadError"] = EngineDownloadEventKind.Error,
        ["aria2.onBtDownloadComplete"] = EngineDownloadEventKind.Completed,
    };

    private readonly IAria2Transport _transport;
    private readonly string _token;
    private readonly TimeSpan _timeout;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _receiveCts = new();
    private long _nextId;
    private volatile bool _closed;

    public Aria2RpcClient(IAria2Transport transport, string secret, TimeSpan? timeout = null, ILogger? logger = null)
    {
        _transport = transport;
        _token = "token:" + secret;
        _timeout = timeout ?? DefaultTimeout;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Raised (on the receive loop) for each aria2 download notification.</summary>
    public event EventHandler<EngineDownloadEvent>? Notification;

    /// <summary>Raised once when the connection ends, for whatever reason.</summary>
    public event EventHandler? Disconnected;

    /// <summary>Whether the connection has ended; every call fails from then on.</summary>
    public bool IsClosed => _closed;

    /// <summary>Starts the receive loop. Call once, after subscribing to the events.</summary>
    public void Start() => _ = Task.Run(ReceiveLoopAsync);

    /// <summary>
    /// Calls <paramref name="method"/> (for example "aria2.tellStatus"). The secret token is added as
    /// the first parameter. Throws <see cref="Aria2RpcException"/> for an error answer,
    /// <see cref="TimeoutException"/> when no answer arrives in time and <see cref="IOException"/>
    /// when the connection is lost.
    /// </summary>
    public Task<JsonNode?> CallAsync(string method, IEnumerable<JsonNode?> parameters, CancellationToken ct) =>
        SendRequestAsync(method, WithToken(parameters), ct);

    /// <summary>
    /// Runs several calls in one request (aria2's system.multicall) and returns their results in order.
    /// aria2 answers them all in one go, so no download changes state between them. Throws
    /// <see cref="Aria2RpcException"/> if any of the calls failed.
    /// </summary>
    public async Task<IReadOnlyList<JsonNode?>> MulticallAsync(IEnumerable<(string Method, JsonNode?[] Parameters)> calls, CancellationToken ct)
    {
        // system.multicall itself takes no token; each inner call carries its own.
        var list = new JsonArray();
        foreach (var (method, parameters) in calls)
        {
            list.Add(new JsonObject { ["methodName"] = method, ["params"] = WithToken(parameters) });
        }

        // Each answer is [result] on success or {"code":..,"message":..} on failure.
        var results = new List<JsonNode?>();
        foreach (var answer in AsArray(await SendRequestAsync("system.multicall", [list], ct)))
        {
            if (answer is not JsonArray { Count: 1 } success)
            {
                throw ToException(answer as JsonObject);
            }

            var result = success[0];
            success.RemoveAt(0); // A JsonNode can belong to only one parent.
            results.Add(result);
        }

        return results;
    }

    private async Task<JsonNode?> SendRequestAsync(string method, JsonArray parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        };

        // The answer is completed on the receive loop; RunContinuationsAsynchronously keeps the
        // caller's code from running on (and blocking) that loop.
        var answer = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;

        try
        {
            // Checked after registering, so a disconnect cannot slip in between and leave this call waiting.
            if (_closed)
            {
                throw new IOException("The connection to aria2 is closed.");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);
            try
            {
                await _transport.SendAsync(request.ToJsonString(), timeoutCts.Token);
                return await answer.Task.WaitAsync(timeoutCts.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
                throw new IOException("The connection to aria2 was lost.", ex);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"aria2 did not answer {method} within {_timeout.TotalSeconds:0.#} s.");
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task<string> AddUriAsync(IEnumerable<string> uris, JsonObject options, CancellationToken ct) =>
        AsString(await CallAsync("aria2.addUri", [new JsonArray([.. uris.Select(u => JsonValue.Create(u))]), options], ct));

    // forcePause/forceRemove instead of pause/remove: the plain versions only add BitTorrent tracker
    // announcements (which can take seconds), Colibri does not use BitTorrent, and the forced ones react at once.
    public Task ForcePauseAsync(string gid, CancellationToken ct) => CallAsync("aria2.forcePause", [gid], ct);

    public Task UnpauseAsync(string gid, CancellationToken ct) => CallAsync("aria2.unpause", [gid], ct);

    public Task ForceRemoveAsync(string gid, CancellationToken ct) => CallAsync("aria2.forceRemove", [gid], ct);

    public Task RemoveDownloadResultAsync(string gid, CancellationToken ct) =>
        CallAsync("aria2.removeDownloadResult", [gid], ct);

    public async Task<JsonObject> TellStatusAsync(string gid, IEnumerable<string> keys, CancellationToken ct) =>
        AsObject(await CallAsync("aria2.tellStatus", [gid, KeyArray(keys)], ct));

    /// <summary>
    /// Status of every download: tellActive, tellWaiting (which includes paused downloads) and
    /// tellStopped, read in one multicall so a download moving between the lists is neither missed nor
    /// counted twice. The waiting and stopped lists are cut at <paramref name="maxPerList"/> entries.
    /// </summary>
    public async Task<IReadOnlyList<JsonObject>> TellAllAsync(int maxPerList, IEnumerable<string> keys, CancellationToken ct)
    {
        var keyList = keys.ToList();
        var lists = await MulticallAsync(
        [
            ("aria2.tellActive", [KeyArray(keyList)]),
            ("aria2.tellWaiting", [0, maxPerList, KeyArray(keyList)]),
            ("aria2.tellStopped", [0, maxPerList, KeyArray(keyList)]),
        ], ct);

        return lists.SelectMany(list => AsArray(list)).OfType<JsonObject>().ToList();
    }

    public async Task<JsonObject> GetGlobalStatAsync(CancellationToken ct) =>
        AsObject(await CallAsync("aria2.getGlobalStat", [], ct));

    public Task ChangeGlobalOptionAsync(JsonObject options, CancellationToken ct) =>
        CallAsync("aria2.changeGlobalOption", [options], ct);

    public Task SaveSessionAsync(CancellationToken ct) => CallAsync("aria2.saveSession", [], ct);

    public Task ShutdownAsync(CancellationToken ct) => CallAsync("aria2.shutdown", [], ct);

    public Task ForceShutdownAsync(CancellationToken ct) => CallAsync("aria2.forceShutdown", [], ct);

    public async Task<string> GetVersionAsync(CancellationToken ct) =>
        AsObject(await CallAsync("aria2.getVersion", [], ct))["version"]?.ToString() ?? string.Empty;

    public void Dispose()
    {
        _receiveCts.Cancel();
        _transport.Dispose();
    }

    private async Task ReceiveLoopAsync()
    {
        Exception? error = null;
        try
        {
            await foreach (var message in _transport.ReceiveAllAsync(_receiveCts.Token))
            {
                HandleMessage(message);
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }

        _closed = true;
        // Debug only: aria2 drops the socket without a close handshake even on a normal shutdown.
        // Whoever owns the client decides whether the disconnect was expected (see Disconnected).
        if (error is not null && !_receiveCts.IsCancellationRequested)
        {
            _logger.LogDebug(error, "The connection to aria2 ended");
        }

        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var answer))
            {
                answer.TrySetException(new IOException("The connection to aria2 was lost.", error));
            }
        }

        try
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A handler for the aria2 disconnect failed");
        }
    }

    private void HandleMessage(string message)
    {
        JsonObject? json;
        try
        {
            json = JsonNode.Parse(message) as JsonObject;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Ignoring a message from aria2 that is not valid JSON");
            return;
        }

        if (json is null)
        {
            return;
        }

        if (json["id"] is { } idNode)
        {
            if (!_pending.TryGetValue(idNode.ToString(), out var answer))
            {
                return; // The caller already gave up (timeout or cancellation).
            }

            if (json["error"] is JsonObject error)
            {
                answer.TrySetException(ToException(error));
            }
            else
            {
                // Detach the result from the message so the caller owns it.
                var result = json["result"];
                json.Remove("result");
                answer.TrySetResult(result);
            }

            return;
        }

        var method = json["method"]?.ToString();
        if (method is null || !NotificationKinds.TryGetValue(method, out var kind))
        {
            return;
        }

        var gid = (json["params"] as JsonArray)?.FirstOrDefault()?["gid"]?.ToString();
        if (string.IsNullOrEmpty(gid))
        {
            return;
        }

        try
        {
            Notification?.Invoke(this, new EngineDownloadEvent(gid, kind));
        }
        catch (Exception ex)
        {
            // A failing subscriber must not stop the receive loop.
            _logger.LogError(ex, "A handler for aria2 notification {Method} failed", method);
        }
    }

    private JsonArray WithToken(IEnumerable<JsonNode?> parameters) => [JsonValue.Create(_token), .. parameters];

    private static Aria2RpcException ToException(JsonObject? error)
    {
        var code = error?["code"]?.GetValueKind() == JsonValueKind.Number ? error["code"]!.GetValue<int>() : 0;
        return new Aria2RpcException(code, error?["message"]?.ToString() ?? "Unknown aria2 error.");
    }

    private static JsonArray KeyArray(IEnumerable<string> keys) => [.. keys.Select(k => JsonValue.Create(k))];

    private static string AsString(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : throw UnexpectedAnswer(node);

    private static JsonObject AsObject(JsonNode? node) => node as JsonObject ?? throw UnexpectedAnswer(node);

    private static JsonArray AsArray(JsonNode? node) => node as JsonArray ?? throw UnexpectedAnswer(node);

    private static FormatException UnexpectedAnswer(JsonNode? node) =>
        new($"Unexpected answer from aria2: {node?.ToJsonString() ?? "null"}");
}
