using System.Text.Json.Nodes;
using Colibri.Core.Engine;

namespace Colibri.Engine.Aria2.Tests;

public class Aria2RpcClientTests
{
    private const string Secret = "s3cret";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (Aria2RpcClient Client, FakeTransport Transport) Create(TimeSpan? timeout = null)
    {
        var transport = new FakeTransport();
        var client = new Aria2RpcClient(transport, Secret, timeout);
        client.Start();
        return (client, transport);
    }

    private static string Reply(JsonObject request, JsonNode? result) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = result }.ToJsonString();

    [Fact]
    public async Task Request_is_json_rpc_2_with_string_id_and_the_token_as_first_parameter()
    {
        var (client, transport) = Create();
        transport.Responder = _ => FakeTransport.Result("OK");

        await client.CallAsync("aria2.tellStatus", ["2089b05ecca3d829", new JsonArray("gid", "status")], Ct);

        var request = Assert.Single(transport.Sent);
        Assert.Equal("2.0", request["jsonrpc"]!.GetValue<string>());
        Assert.Equal("aria2.tellStatus", request["method"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(request["id"]!.GetValue<string>()));
        Assert.Equal("""["token:s3cret","2089b05ecca3d829",["gid","status"]]""", request["params"]!.ToJsonString());
    }

    [Fact]
    public async Task Each_call_gets_a_new_id()
    {
        var (client, transport) = Create();
        transport.Responder = _ => FakeTransport.Result("OK");

        await client.CallAsync("aria2.getGlobalStat", [], Ct);
        await client.CallAsync("aria2.getGlobalStat", [], Ct);

        var ids = transport.Sent.Select(r => r["id"]!.GetValue<string>()).ToList();
        Assert.Equal(2, ids.Distinct().Count());
    }

    [Fact]
    public async Task Out_of_order_answers_reach_the_right_callers()
    {
        var (client, transport) = Create();

        var first = client.CallAsync("aria2.getVersion", [], Ct);
        var firstRequest = await transport.NextRequestAsync();
        var second = client.CallAsync("aria2.getGlobalStat", [], Ct);
        var secondRequest = await transport.NextRequestAsync();

        transport.Push(Reply(secondRequest, "second"));
        transport.Push(Reply(firstRequest, "first"));

        Assert.Equal("first", (await first)!.GetValue<string>());
        Assert.Equal("second", (await second)!.GetValue<string>());
    }

    [Fact]
    public async Task Error_answer_becomes_Aria2RpcException_with_code_and_message()
    {
        var (client, transport) = Create();
        transport.Responder = _ => FakeTransport.Error(1, "GID 2089b05ecca3d829 is not found");

        var ex = await Assert.ThrowsAsync<Aria2RpcException>(() => client.UnpauseAsync("2089b05ecca3d829", Ct));

        Assert.Equal(1, ex.Code);
        Assert.Equal("GID 2089b05ecca3d829 is not found", ex.Message);
        Assert.True(ex.IsGidNotFound);
    }

    [Fact]
    public void Other_errors_are_not_reported_as_unknown_gid()
    {
        Assert.False(new Aria2RpcException(1, "GID#2089b05ecca3d829 cannot be paused now").IsGidNotFound);
    }

    [Fact]
    public async Task Call_without_answer_times_out()
    {
        var (client, _) = Create(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<TimeoutException>(() => client.GetVersionAsync(Ct));
    }

    [Fact]
    public async Task Cancelled_call_throws_OperationCanceledException_not_timeout()
    {
        var (client, transport) = Create();
        using var cts = new CancellationTokenSource();

        var call = client.GetVersionAsync(cts.Token);
        await transport.NextRequestAsync();
        cts.Cancel();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => call);
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    [Fact]
    public async Task Socket_drop_fails_pending_calls_and_later_calls_fail_at_once()
    {
        var (client, transport) = Create();
        var disconnected = new TaskCompletionSource();
        client.Disconnected += (_, _) => disconnected.TrySetResult();

        var pending = client.GetVersionAsync(Ct);
        await transport.NextRequestAsync();
        transport.Drop();

        await Assert.ThrowsAsync<IOException>(() => pending);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await Assert.ThrowsAsync<IOException>(() => client.GetVersionAsync(Ct));
    }

    [Theory]
    [InlineData("aria2.onDownloadStart", EngineDownloadEventKind.Started)]
    [InlineData("aria2.onDownloadPause", EngineDownloadEventKind.Paused)]
    [InlineData("aria2.onDownloadStop", EngineDownloadEventKind.Stopped)]
    [InlineData("aria2.onDownloadComplete", EngineDownloadEventKind.Completed)]
    [InlineData("aria2.onDownloadError", EngineDownloadEventKind.Error)]
    [InlineData("aria2.onBtDownloadComplete", EngineDownloadEventKind.ContentCompleted)]
    public async Task Notification_raises_event_with_gid_and_kind(string method, EngineDownloadEventKind kind)
    {
        var (client, transport) = Create();
        var received = new TaskCompletionSource<EngineDownloadEvent>();
        client.Notification += (_, e) => received.TrySetResult(e);

        transport.Push($$"""{"jsonrpc":"2.0","method":"{{method}}","params":[{"gid":"2089b05ecca3d829"}]}""");

        var e = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(new EngineDownloadEvent("2089b05ecca3d829", kind), e);
    }

    [Fact]
    public async Task Invalid_json_and_unknown_notifications_are_skipped_and_the_loop_keeps_running()
    {
        var (client, transport) = Create();
        var received = new List<EngineDownloadEvent>();
        client.Notification += (_, e) => received.Add(e);
        transport.Push("{ not json");
        transport.Push("""{"jsonrpc":"2.0","method":"aria2.onSomethingNew","params":[{"gid":"aaaaaaaaaaaaaaaa"}]}""");

        // A call answered after the bad messages proves the loop survived them.
        transport.Responder = _ => FakeTransport.Result(new JsonObject { ["version"] = "1.37.0" });
        Assert.Equal("1.37.0", await client.GetVersionAsync(Ct));
        Assert.Empty(received);
    }

    [Fact]
    public async Task A_throwing_notification_handler_does_not_stop_the_loop()
    {
        var (client, transport) = Create();
        client.Notification += (_, _) => throw new InvalidOperationException("handler bug");
        transport.Push("""{"jsonrpc":"2.0","method":"aria2.onDownloadStart","params":[{"gid":"aaaaaaaaaaaaaaaa"}]}""");

        transport.Responder = _ => FakeTransport.Result(new JsonObject { ["version"] = "1.37.0" });
        Assert.Equal("1.37.0", await client.GetVersionAsync(Ct));
    }

    [Fact]
    public async Task Dispose_closes_the_transport()
    {
        var (client, transport) = Create();

        client.Dispose();

        Assert.True(transport.Disposed);
        await Assert.ThrowsAsync<IOException>(() => client.GetVersionAsync(Ct));
    }

    [Fact]
    public async Task A_failing_send_is_reported_as_a_lost_connection()
    {
        var (client, transport) = Create();
        transport.SendFailure = new System.Net.WebSockets.WebSocketException("Connection reset");

        var ex = await Assert.ThrowsAsync<IOException>(() => client.GetVersionAsync(Ct));

        Assert.IsType<System.Net.WebSockets.WebSocketException>(ex.InnerException);
    }

    [Fact]
    public async Task Multicall_puts_the_token_in_each_inner_call_and_returns_results_in_order()
    {
        var (client, transport) = Create();
        transport.Responder = _ => FakeTransport.Result(JsonNode.Parse("""[["a"],["b"]]"""));

        var results = await client.MulticallAsync([("aria2.getVersion", []), ("aria2.tellStatus", ["2089b05ecca3d829"])], Ct);

        Assert.Equal(["a", "b"], results.Select(r => r!.GetValue<string>()));
        var request = Assert.Single(transport.Sent);
        Assert.Equal("system.multicall", request["method"]!.GetValue<string>());
        Assert.Equal(
            """[[{"methodName":"aria2.getVersion","params":["token:s3cret"]},{"methodName":"aria2.tellStatus","params":["token:s3cret","2089b05ecca3d829"]}]]""",
            request["params"]!.ToJsonString());
    }

    [Fact]
    public async Task Multicall_throws_when_an_inner_call_failed()
    {
        var (client, transport) = Create();
        transport.Responder = _ => FakeTransport.Result(JsonNode.Parse("""[["a"],{"code":1,"message":"GID x is not found"}]"""));

        var ex = await Assert.ThrowsAsync<Aria2RpcException>(() =>
            client.MulticallAsync([("aria2.getVersion", []), ("aria2.tellStatus", ["x"])], Ct));

        Assert.Equal("GID x is not found", ex.Message);
    }
}
