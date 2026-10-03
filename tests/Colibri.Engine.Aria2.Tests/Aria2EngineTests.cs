using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Colibri.Core.Engine;
using Colibri.Core.Models;

namespace Colibri.Engine.Aria2.Tests;

public class Aria2EngineTests
{
    private static readonly EngineOptions Options = new(MaxConcurrentDownloads: 3, ConnectionsPerServer: 8, GlobalSpeedLimitBytesPerSecond: 0);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (Aria2Engine Engine, FakeTransport Transport) Create(Func<JsonObject, JsonObject?> responder)
    {
        var transport = new FakeTransport { Responder = responder };
        var client = new Aria2RpcClient(transport, "secret");
        var engine = new Aria2Engine(client, Options);
        client.Start();
        return (engine, transport);
    }

    private static string Method(JsonObject request) => request["method"]!.GetValue<string>();

    private static JsonArray Params(JsonObject request) => request["params"]!.AsArray();

    private static JsonObject StatusJson(string gid, string status) =>
        new() { ["gid"] = gid, ["status"] = status, ["totalLength"] = "100", ["completedLength"] = "10" };

    // Adds a download and returns the aria2.addUri request the engine sent.
    private static async Task<(string Gid, JsonObject Request)> AddAsync(
        DownloadRequest request, string folder = "/downloads", string fileName = "file.zip", string? handle = null, bool startPaused = false)
    {
        var (engine, transport) = Create(r => FakeTransport.Result(Params(r)[2]!["gid"]!.DeepClone()));
        var gid = await engine.AddAsync(request, folder, fileName, handle, startPaused, Ct);
        return (gid, transport.Sent.Single(r => Method(r) == "aria2.addUri"));
    }

    private static JsonObject AddOptions(JsonObject addRequest) => Params(addRequest)[2]!.AsObject();

    private static List<string> HeaderLines(JsonObject addRequest) =>
        AddOptions(addRequest)["header"]?.AsArray().Select(h => h!.GetValue<string>()).ToList() ?? [];

    [Theory]
    [InlineData("http://example.com/a", true)]
    [InlineData("https://example.com/a", true)]
    [InlineData("ftp://example.com/a", true)]
    [InlineData("HTTPS://example.com/a", true)]
    [InlineData("file:///tmp/a", false)]
    [InlineData("magnet:?xt=urn:btih:abc", false)]
    [InlineData("sftp://example.com/a", false)]
    public void Handles_http_https_and_ftp_only(string url, bool expected)
    {
        var (engine, _) = Create(_ => null);

        Assert.Equal(expected, engine.CanHandle(new DownloadRequest { Uri = new Uri(url) }));
    }

    [Fact]
    public async Task Add_returns_a_caller_generated_16_hex_gid_and_sends_it_to_aria2()
    {
        var (gid, request) = await AddAsync(new DownloadRequest { Uri = new Uri("https://example.com/file.zip") });

        Assert.Matches(new Regex("^[0-9a-f]{16}$"), gid);
        Assert.Equal(gid, AddOptions(request)["gid"]!.GetValue<string>());
    }

    [Fact]
    public async Task Each_add_gets_a_different_gid()
    {
        var (first, _) = await AddAsync(new DownloadRequest { Uri = new Uri("https://example.com/a") });
        var (second, _) = await AddAsync(new DownloadRequest { Uri = new Uri("https://example.com/a") });

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Add_with_a_handle_reuses_it_as_the_gid()
    {
        var (gid, request) = await AddAsync(new DownloadRequest { Uri = new Uri("https://example.com/a") }, handle: "0123456789abcdef");

        Assert.Equal("0123456789abcdef", gid);
        Assert.Equal("0123456789abcdef", AddOptions(request)["gid"]!.GetValue<string>());
    }

    [Fact]
    public async Task Add_paused_sets_the_pause_option_and_a_normal_add_does_not()
    {
        var (_, paused) = await AddAsync(new DownloadRequest { Uri = new Uri("https://example.com/a") }, startPaused: true);
        var (_, normal) = await AddAsync(new DownloadRequest { Uri = new Uri("https://example.com/a") });

        Assert.Equal("true", AddOptions(paused)["pause"]!.GetValue<string>());
        Assert.Null(AddOptions(normal)["pause"]);
    }

    [Fact]
    public async Task Add_sends_token_url_and_folder_name_and_connection_options()
    {
        var (_, request) = await AddAsync(new DownloadRequest { Uri = new Uri("https://example.com/path/file.zip?x=1") }, "/home/me/Downloads", "file (1).zip");

        Assert.Equal("token:secret", Params(request)[0]!.GetValue<string>());
        Assert.Equal("""["https://example.com/path/file.zip?x=1"]""", Params(request)[1]!.ToJsonString());
        var options = AddOptions(request);
        Assert.Equal("/home/me/Downloads", options["dir"]!.GetValue<string>());
        Assert.Equal("file (1).zip", options["out"]!.GetValue<string>());
        Assert.Equal("8", options["max-connection-per-server"]!.GetValue<string>());
        Assert.Equal("8", options["split"]!.GetValue<string>());
        Assert.Null(options["header"]);
        Assert.Null(options["referer"]);
        Assert.Null(options["user-agent"]);
    }

    [Fact]
    public async Task Add_sends_headers_cookie_referer_and_user_agent()
    {
        var request = new DownloadRequest
        {
            Uri = new Uri("https://example.com/file.zip"),
            Headers = HttpHeaders.Copy([new("Authorization", "Bearer abc"), new("Accept-Language", "es-ES")]),
            Cookies = "session=1; theme=dark",
            Referrer = "https://example.com/page",
            UserAgent = "Mozilla/5.0 Test",
        };

        var (_, add) = await AddAsync(request);

        Assert.Equal(["Authorization: Bearer abc", "Accept-Language: es-ES", "Cookie: session=1; theme=dark"], HeaderLines(add));
        Assert.Equal("https://example.com/page", AddOptions(add)["referer"]!.GetValue<string>());
        Assert.Equal("Mozilla/5.0 Test", AddOptions(add)["user-agent"]!.GetValue<string>());
    }

    [Fact]
    public async Task Dedicated_fields_win_over_the_same_header_in_the_dictionary()
    {
        var request = new DownloadRequest
        {
            Uri = new Uri("https://example.com/file.zip"),
            Headers = HttpHeaders.Copy([new("cookie", "old=1"), new("Referer", "https://old"), new("User-Agent", "old")]),
            Cookies = "new=1",
            Referrer = "https://new",
            UserAgent = "new",
        };

        var (_, add) = await AddAsync(request);

        Assert.Equal(["Cookie: new=1"], HeaderLines(add));
    }

    [Fact]
    public async Task Invalid_headers_are_not_sent()
    {
        var request = new DownloadRequest
        {
            Uri = new Uri("https://example.com/file.zip"),
            Headers = HttpHeaders.Copy(
            [
                new("X-Good", "yes"),
                new("Bad Name", "value"),
                new("X-Injected", "a\r\nEvil: 1"),
            ]),
            Cookies = "a=1\nEvil: 2",
            UserAgent = "agent\r\n",
            Referrer = "https://example.com/\0",
        };

        var (_, add) = await AddAsync(request);

        Assert.Equal(["X-Good: yes"], HeaderLines(add));
        Assert.Null(AddOptions(add)["user-agent"]);
        Assert.Null(AddOptions(add)["referer"]);
    }

    [Fact]
    public async Task Unicode_host_is_sent_as_punycode()
    {
        var (_, add) = await AddAsync(new DownloadRequest { Uri = new Uri("https://bücher.example/ñandú.zip") });

        var url = Params(add)[1]![0]!.GetValue<string>();
        Assert.Equal("https://xn--bcher-kva.example/%C3%B1and%C3%BA.zip", url);
    }

    [Fact]
    public async Task Add_rejects_schemes_it_cannot_handle()
    {
        var (engine, transport) = Create(_ => null);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            engine.AddAsync(new DownloadRequest { Uri = new Uri("file:///tmp/a") }, "/d", "a", null, false, Ct));
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task Pause_uses_forcePause_and_resume_uses_unpause()
    {
        var (engine, transport) = Create(r => FakeTransport.Result(Params(r)[1]!.DeepClone()));

        await engine.PauseAsync("aaaaaaaaaaaaaaaa", Ct);
        await engine.ResumeAsync("aaaaaaaaaaaaaaaa", Ct);

        Assert.Equal(["aria2.forcePause", "aria2.unpause"], transport.Sent.Select(Method));
        Assert.All(transport.Sent, r => Assert.Equal("aaaaaaaaaaaaaaaa", Params(r)[1]!.GetValue<string>()));
    }

    [Fact]
    public async Task Pause_in_the_wrong_state_throws_EngineOperationException()
    {
        var (engine, _) = Create(_ => FakeTransport.Error(1, "GID#aaaaaaaaaaaaaaaa cannot be paused now"));

        var ex = await Assert.ThrowsAsync<EngineOperationException>(() => engine.PauseAsync("aaaaaaaaaaaaaaaa", Ct));

        Assert.Contains("cannot be paused now", ex.Message);
        Assert.IsType<Aria2RpcException>(ex.InnerException);
    }

    [Fact]
    public async Task Status_of_an_unknown_gid_is_null()
    {
        var (engine, _) = Create(_ => FakeTransport.Error(1, "GID aaaaaaaaaaaaaaaa is not found"));

        Assert.Null(await engine.GetStatusAsync("aaaaaaaaaaaaaaaa", Ct));
    }

    [Fact]
    public async Task Status_asks_only_for_the_needed_keys()
    {
        var (engine, transport) = Create(r => FakeTransport.Result(StatusJson("aaaaaaaaaaaaaaaa", "paused")));

        var status = await engine.GetStatusAsync("aaaaaaaaaaaaaaaa", Ct);

        Assert.Equal(EngineDownloadState.Paused, status!.State);
        var keys = Params(transport.Sent.Single())[2]!.AsArray().Select(k => k!.GetValue<string>());
        Assert.Equal(Aria2Status.Keys, keys);
    }

    [Fact]
    public async Task Other_status_errors_are_not_hidden()
    {
        var (engine, _) = Create(_ => FakeTransport.Error(1, "Something else broke"));

        await Assert.ThrowsAsync<Aria2RpcException>(() => engine.GetStatusAsync("aaaaaaaaaaaaaaaa", Ct));
    }

    [Fact]
    public async Task GetAll_reads_active_waiting_and_stopped_lists_in_one_multicall()
    {
        // Answers a system.multicall: one [result] per inner call, in order.
        static JsonObject? Respond(JsonObject request)
        {
            var results = new JsonArray();
            foreach (var call in Params(request)[0]!.AsArray())
            {
                JsonArray list = call!["methodName"]!.GetValue<string>() switch
                {
                    "aria2.tellActive" => [StatusJson("aaaaaaaaaaaaaaaa", "active")],
                    "aria2.tellWaiting" => [StatusJson("bbbbbbbbbbbbbbbb", "paused"), StatusJson("cccccccccccccccc", "waiting")],
                    "aria2.tellStopped" => [StatusJson("dddddddddddddddd", "complete")],
                    _ => throw new InvalidOperationException(),
                };
                results.Add(new JsonArray(list));
            }

            return FakeTransport.Result(results);
        }

        var (engine, transport) = Create(Respond);

        var all = await engine.GetAllAsync(Ct);

        Assert.Equal(
            [("aaaaaaaaaaaaaaaa", EngineDownloadState.Active), ("bbbbbbbbbbbbbbbb", EngineDownloadState.Paused),
             ("cccccccccccccccc", EngineDownloadState.Waiting), ("dddddddddddddddd", EngineDownloadState.Complete)],
            all.Select(s => (s.Handle, s.State)));
        var request = Assert.Single(transport.Sent);
        Assert.Equal("system.multicall", Method(request));
        var waiting = Params(request)[0]![1]!["params"]!.AsArray();
        Assert.Equal(0, waiting[1]!.GetValue<int>());
        Assert.Equal(1000, waiting[2]!.GetValue<int>());
    }

    [Fact]
    public async Task Global_stats_are_parsed_from_strings()
    {
        var (engine, _) = Create(_ => FakeTransport.Result(new JsonObject
        {
            ["downloadSpeed"] = "123456", ["numActive"] = "2", ["numWaiting"] = "5", ["numStopped"] = "9",
        }));

        Assert.Equal(new EngineGlobalStats(123456, 2, 5), await engine.GetGlobalStatsAsync(Ct));
    }

    [Fact]
    public async Task ApplyOptions_changes_global_options_and_affects_later_adds()
    {
        var (engine, transport) = Create(r => Method(r) == "aria2.addUri"
            ? FakeTransport.Result(Params(r)[2]!["gid"]!.DeepClone())
            : FakeTransport.Result("OK"));

        await engine.ApplyOptionsAsync(new EngineOptions(MaxConcurrentDownloads: 2, ConnectionsPerServer: 4, GlobalSpeedLimitBytesPerSecond: 1_048_576), Ct);
        await engine.AddAsync(new DownloadRequest { Uri = new Uri("https://example.com/a") }, "/d", "a", null, false, Ct);

        var change = transport.Sent.Single(r => Method(r) == "aria2.changeGlobalOption");
        Assert.Equal("""{"max-concurrent-downloads":"2","max-overall-download-limit":"1048576"}""", Params(change)[1]!.ToJsonString());
        var add = transport.Sent.Single(r => Method(r) == "aria2.addUri");
        Assert.Equal("4", AddOptions(add)["max-connection-per-server"]!.GetValue<string>());
    }

    [Fact]
    public async Task Remove_of_an_unfinished_download_force_removes_then_clears_the_result()
    {
        // Like aria2: the status reads "removed" only after forceRemove.
        var removed = false;
        JsonObject Respond(JsonObject request)
        {
            switch (Method(request))
            {
                case "aria2.tellStatus":
                    return FakeTransport.Result(StatusJson("aaaaaaaaaaaaaaaa", removed ? "removed" : "paused"));
                case "aria2.forceRemove":
                    removed = true;
                    return FakeTransport.Result("aaaaaaaaaaaaaaaa");
                default:
                    return FakeTransport.Result("OK");
            }
        }

        var (engine, transport) = Create(Respond);

        await engine.RemoveAsync("aaaaaaaaaaaaaaaa", Ct);

        Assert.Equal(
            ["aria2.tellStatus", "aria2.forceRemove", "aria2.tellStatus", "aria2.removeDownloadResult"],
            transport.Sent.Select(Method));
    }

    [Fact]
    public async Task Remove_succeeds_when_the_download_finishes_just_before_forceRemove()
    {
        var statusReads = 0;
        JsonObject Respond(JsonObject request) => Method(request) switch
        {
            "aria2.tellStatus" => FakeTransport.Result(StatusJson("aaaaaaaaaaaaaaaa", ++statusReads == 1 ? "active" : "complete")),
            "aria2.forceRemove" => FakeTransport.Error(1, "Active Download not found for GID#aaaaaaaaaaaaaaaa"),
            _ => FakeTransport.Result("OK"),
        };
        var (engine, transport) = Create(Respond);

        await engine.RemoveAsync("aaaaaaaaaaaaaaaa", Ct);

        Assert.Equal("aria2.removeDownloadResult", transport.Sent.Select(Method).Last());
    }

    [Fact]
    public async Task Remove_refused_while_still_running_throws_EngineOperationException()
    {
        var (engine, _) = Create(r => Method(r) == "aria2.forceRemove"
            ? FakeTransport.Error(1, "something went wrong")
            : FakeTransport.Result(StatusJson("aaaaaaaaaaaaaaaa", "active")));

        await Assert.ThrowsAsync<EngineOperationException>(() => engine.RemoveAsync("aaaaaaaaaaaaaaaa", Ct));
    }

    [Fact]
    public async Task Remove_of_a_finished_download_only_clears_the_result()
    {
        var (engine, transport) = Create(r => Method(r) == "aria2.tellStatus"
            ? FakeTransport.Result(StatusJson("aaaaaaaaaaaaaaaa", "complete"))
            : FakeTransport.Result("OK"));

        await engine.RemoveAsync("aaaaaaaaaaaaaaaa", Ct);

        Assert.Equal(["aria2.tellStatus", "aria2.removeDownloadResult"], transport.Sent.Select(Method));
    }

    [Fact]
    public async Task Remove_of_an_unknown_gid_does_nothing()
    {
        var (engine, transport) = Create(_ => FakeTransport.Error(1, "GID aaaaaaaaaaaaaaaa is not found"));

        await engine.RemoveAsync("aaaaaaaaaaaaaaaa", Ct);

        Assert.Equal(["aria2.tellStatus"], transport.Sent.Select(Method));
    }

    [Fact]
    public async Task Notifications_are_raised_as_engine_download_events()
    {
        var (engine, transport) = Create(_ => null);
        var received = new TaskCompletionSource<EngineDownloadEvent>();
        engine.DownloadEvent += (_, e) => received.TrySetResult(e);

        transport.Push("""{"jsonrpc":"2.0","method":"aria2.onDownloadComplete","params":[{"gid":"aaaaaaaaaaaaaaaa"}]}""");

        Assert.Equal(new EngineDownloadEvent("aaaaaaaaaaaaaaaa", EngineDownloadEventKind.Completed),
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }
}
