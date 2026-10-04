using System.Text;
using System.Text.Json.Nodes;
using Colibri.Core.Engine;
using Colibri.Core.Torrents;

namespace Colibri.Engine.Aria2.Tests;

public sealed class Aria2TorrentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static byte[] Metainfo => Encoding.ASCII.GetBytes("d4:infod6:lengthi3e4:name5:a.txt12:piece lengthi16384e6:pieces20:12345678901234567890ee");

    [Fact]
    public async Task Proxy_blocks_torrent_content_and_metadata_without_any_rpc()
    {
        using var transport = new FakeTransport();
        using var client = new Aria2RpcClient(transport, "secret");
        using var engine = new Aria2Engine(client, new(3, 8, 0)
        { NetworkPolicy = new() { Proxy = new() { Endpoint = new("http://proxy.test:8080") } } });
        await Assert.ThrowsAsync<EngineOperationException>(() => engine.PreviewMagnetAsync("magnet:?xt=urn:btih:" + new string('0', 40), Ct));
        await Assert.ThrowsAsync<EngineOperationException>(() => engine.AddAsync(new()
        { Uri = new("magnet:?xt=urn:btih:" + new string('0', 40)), Torrent = new(Metainfo, [1], new()) }, "/downloads", "content", null, true, Ct));
        Assert.Empty(transport.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selected_content_uses_addTorrent_index_mapping_and_seeding_is_opt_in(bool seed)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "colibri-torrent-test-" + Guid.NewGuid().ToString("N"));
        using var transport = new FakeTransport { Responder = _ => FakeTransport.Result("0123456789abcdef") };
        using var client = new Aria2RpcClient(transport, "secret");
        client.Start();
        using var engine = new Aria2Engine(client, new(3, 8, 0));
        try
        {
            await engine.AddAsync(new() { Uri = new("magnet:?xt=urn:btih:" + new string('0', 40)),
                Torrent = new(Metainfo, [1], new(Enabled: seed)) }, directory, "content", "0123456789abcdef", true, Ct);
            var call = Assert.Single(transport.Sent);
            Assert.Equal("aria2.addTorrent", call["method"]!.ToString());
            var options = call["params"]![3]!;
            Assert.Equal(seed ? "60" : "0", options["seed-time"]!.ToString());
            Assert.Equal("true", options["pause"]!.ToString());
            Assert.Equal("1", options["select-file"]!.ToString());
            Assert.Equal("1=content/a.txt", options["index-out"]![0]!.ToString());
            Assert.Equal(Metainfo, Convert.FromBase64String(call["params"]![1]!.ToString()));
        }
        finally
        {
            if (Directory.Exists(Path.Combine(directory, "content"))) Directory.Delete(Path.Combine(directory, "content"));
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Metadata_preview_removes_owned_job_and_files_before_returning_or_cancellation(bool cancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var root = Path.Combine(AppContext.BaseDirectory, "colibri-metadata-test-" + Guid.NewGuid().ToString("N"));
        var hash = TorrentMetainfo.Parse(Metainfo).Metadata.InfoHash;
        var state = "complete";
        using var transport = new FakeTransport
        {
            Responder = request =>
            {
                var method = request["method"]!.ToString();
                if (method == "aria2.addUri")
                {
                    var options = request["params"]![2]!;
                    Assert.Equal("true", options["bt-metadata-only"]!.ToString());
                    Assert.Equal("true", options["pause-metadata"]!.ToString());
                    File.WriteAllBytes(Path.Combine(options["dir"]!.ToString(), hash + ".torrent"), Metainfo);
                    if (cancel) cancellation.Cancel();
                    return FakeTransport.Result(options["gid"]!.ToString());
                }
                if (method == "aria2.tellStatus") return FakeTransport.Result(new JsonObject { ["status"] = state });
                if (method == "aria2.forceRemove") state = "removed";
                return FakeTransport.Result("OK");
            },
        };
        using var client = new Aria2RpcClient(transport, "secret");
        client.Start();
        using var engine = new Aria2Engine(client, new(3, 8, 0), root);
        try
        {
            var magnet = "magnet:?xt=urn:btih:" + hash + "&tr=http%3A%2F%2F127.0.0.1%3A8080%2Fannounce";
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.PreviewMagnetAsync(magnet, cancellation.Token));
            else Assert.Equal(hash, (await engine.PreviewMagnetAsync(magnet, cancellation.Token)).Metadata.InfoHash);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            Assert.Contains(transport.Sent, request => request["method"]!.ToString() == "aria2.removeDownloadResult");
            Assert.DoesNotContain(transport.Sent, request => request["method"]!.ToString() == "aria2.addTorrent");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root); }
    }

    [Fact]
    public async Task Stopping_known_reconstructed_paused_torrent_does_not_require_native_length_probe()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "colibri-torrent-stop-" + Guid.NewGuid().ToString("N"));
        var state = "paused";
        using var transport = new FakeTransport { Responder = request =>
        {
            var method = request["method"]!.ToString();
            if (method == "aria2.addTorrent") return FakeTransport.Result("0123456789abcdef");
            if (method == "aria2.tellStatus") return FakeTransport.Result(new JsonObject
            { ["gid"] = "0123456789abcdef", ["status"] = state, ["totalLength"] = "0", ["completedLength"] = "0" });
            if (method == "aria2.forceRemove") state = "removed";
            return FakeTransport.Result("OK");
        } };
        using var client = new Aria2RpcClient(transport, "secret");
        client.Start();
        using var engine = new Aria2Engine(client, new(3, 8, 0));
        try
        {
            await engine.AddAsync(new() { Uri = new("magnet:?xt=urn:btih:" + new string('0', 40)),
                Torrent = new(Metainfo, [1], new(Enabled: true)) }, directory, "content", "0123456789abcdef", true, Ct);
            await engine.StopSeedingAsync("0123456789abcdef", Ct);
            Assert.Contains(transport.Sent, request => request["method"]!.ToString() == "aria2.forceRemove");
        }
        finally
        {
            if (Directory.Exists(Path.Combine(directory, "content"))) Directory.Delete(Path.Combine(directory, "content"));
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [Fact]
    public void Selected_torrent_status_reports_uploads_and_seeding_separately()
    {
        var status = Aria2Status.Parse(JsonNode.Parse("""
            {"gid":"0123456789abcdef","status":"active","totalLength":"100","completedLength":"50",
             "uploadSpeed":"12","uploadLength":"345","bittorrent":{},"files":[
             {"selected":"true","length":"3","completedLength":"3","path":"/downloads/content/a.txt"},
             {"selected":"false","length":"97","completedLength":"47","path":"/downloads/content/b.txt"}]}
            """)!.AsObject());
        Assert.Equal(3, status.TotalBytes);
        Assert.Equal(3, status.CompletedBytes);
        Assert.Equal(12, status.UploadSpeed);
        Assert.Equal(345, status.UploadedBytes);
        Assert.True(status.IsSeeding);
        Assert.Equal(EngineDownloadState.Active, status.State);
    }
}
