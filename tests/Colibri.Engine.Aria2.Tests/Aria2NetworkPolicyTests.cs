using System.Text.Json.Nodes;
using Colibri.Core.Engine;
using Colibri.Core.Models;
using Colibri.Core.Network;

namespace Colibri.Engine.Aria2.Tests;

public sealed class Aria2NetworkPolicyTests
{
    [Fact]
    public async Task Strict_adapter_blocks_add_start_and_resume_without_rpc()
    {
        using var transport = new FakeTransport();
        using var client = new Aria2RpcClient(transport, "secret");
        using var engine = new Aria2Engine(client, new(3, 8, 0)
        { NetworkPolicy = new() { RequiredInterfaceId = "disconnected-adapter" } });
        await Assert.ThrowsAsync<EngineOperationException>(() => engine.StartAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<EngineOperationException>(() => engine.AddAsync(
            new() { Uri = new("https://example.test/file") }, "/downloads", "file", null, false, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<EngineOperationException>(() => engine.ResumeAsync("0123456789abcdef", TestContext.Current.CancellationToken));
        Assert.Empty(transport.Sent);
    }

    [Theory]
    [InlineData("https://proxy.test:443")]
    [InlineData("socks5://proxy.test:1080")]
    [InlineData("http://user:password@proxy.test:8080")]
    public void Unsupported_or_embedded_proxy_credentials_are_rejected(string endpoint)
    {
        var options = new JsonObject();
        Assert.Throws<EngineOperationException>(() => Aria2NetworkPolicy.Apply(options,
            new() { Proxy = new() { Endpoint = new(endpoint) } }));
        Assert.Empty(options);
    }

    [Fact]
    public void Proxy_credentials_are_separate_from_endpoint_and_absent_from_arguments()
    {
        var policy = new DownloadNetworkPolicy
        { Proxy = new() { Endpoint = new("http://proxy.test:8080"), UserName = "user", Password = "private-password" } };
        var options = new JsonObject();
        Aria2NetworkPolicy.Apply(options, policy);
        Assert.Equal("http://proxy.test:8080", options["https-proxy"]!.ToString());
        Assert.Equal("private-password", options["https-proxy-passwd"]!.ToString());
        Assert.Equal("", options["no-proxy"]!.ToString());
        Assert.Equal("false", options["follow-torrent"]!.ToString());
        Assert.DoesNotContain("private-password", policy.ToString());
        var arguments = Aria2Arguments.Build(6800, "/config", 123, "/log", new(3, 8, 0) { NetworkPolicy = policy });
        Assert.DoesNotContain(arguments, argument => argument.Contains("private-password", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("--log=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Editing_policy_pauses_before_rpc_change_and_never_resumes()
    {
        using var transport = new FakeTransport
        {
            Responder = request => FakeTransport.Result(request["method"]!.ToString() == "aria2.tellStatus"
                ? new JsonObject { ["status"] = "paused" } : JsonValue.Create("OK")),
        };
        using var client = new Aria2RpcClient(transport, "secret");
        client.Start();
        using var engine = new Aria2Engine(client, new(3, 8, 0));
        await engine.ApplyNetworkPolicyAsync("0123456789abcdef", new()
        { Proxy = new() { Endpoint = new("http://proxy.test:8080") } }, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "aria2.forcePause", "aria2.tellStatus", "aria2.changeOption" },
            transport.Sent.Select(request => request["method"]!.ToString()));
    }

    [Fact]
    public void Direct_override_clears_every_proxy_option()
    {
        var options = new JsonObject { ["https-proxy"] = "http://inherited.test:8080" };
        Aria2NetworkPolicy.Apply(options, new());
        foreach (var protocol in new[] { "all", "http", "https", "ftp" })
        {
            Assert.Equal("", options[$"{protocol}-proxy"]!.ToString());
            Assert.Equal("", options[$"{protocol}-proxy-passwd"]!.ToString());
        }
    }
}
