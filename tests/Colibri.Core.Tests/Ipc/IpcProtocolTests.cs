using System.Text;
using Colibri.Core.Ipc;
using Colibri.Core.Models;

namespace Colibri.Core.Tests.Ipc;

public class IpcProtocolTests
{
    private static IpcRequest Parse(string line)
    {
        Assert.True(IpcProtocol.TryParseRequest(line, out var request, out var error), error);
        return request;
    }

    private static string Reject(string line)
    {
        Assert.False(IpcProtocol.TryParseRequest(line, out _, out var error));
        return error;
    }

    [Fact]
    public void Activate_with_arguments()
    {
        var request = Assert.IsType<ActivateRequest>(Parse("""{"type":"activate","args":["--minimized"]}"""));
        Assert.Equal(["--minimized"], request.Args);
    }

    [Fact]
    public void Activate_without_arguments()
    {
        Assert.Empty(Assert.IsType<ActivateRequest>(Parse("""{"type":"activate"}""")).Args);
    }

    [Fact]
    public void Add_with_every_field()
    {
        var request = Assert.IsType<AddRequest>(Parse("""
            {"type":"add","url":"https://example.com/a.zip","finalUrl":"https://cdn.example.com/a.zip",
             "fileName":"a.zip","referrer":"https://example.com/","cookies":"sid=1","userAgent":"UA/1",
             "size":1234,"mimeType":"application/zip","headers":{"Authorization":"Bearer x","Accept":"*/*"},
             "somethingNew":42}
            """.ReplaceLineEndings(string.Empty)));

        Assert.Equal("https://example.com/a.zip", request.Url);
        Assert.Equal("https://cdn.example.com/a.zip", request.FinalUrl);
        var context = request.Context;
        Assert.Equal("a.zip", context.FileName);
        Assert.Equal("https://example.com/", context.Referrer);
        Assert.Equal("sid=1", context.Cookies);
        Assert.Equal("UA/1", context.UserAgent);
        Assert.Equal(1234, context.Size);
        Assert.Equal("application/zip", context.MimeType);
        Assert.Equal("Bearer x", context.Headers["authorization"]);
        Assert.Equal(2, context.Headers.Count);
    }

    [Fact]
    public void Add_drops_headers_that_must_not_reach_the_engine()
    {
        var request = Assert.IsType<AddRequest>(Parse(
            """{"type":"add","url":"https://example.com/a","headers":{"Range":"bytes=0-","Cookie":"x=1","Proxy-Authorization":"p","X-Ok":"1"}}"""));

        Assert.Equal(["X-Ok"], request.Context.Headers.Keys);
    }

    [Fact]
    public void Round_trip_through_serialize_and_parse()
    {
        var original = new AddRequest("https://example.com/a.zip", null, new LinkContext
        {
            FileName = "naïve \"quoted\".zip",
            Size = 10,
            Headers = new Dictionary<string, string> { ["X-A"] = "1" },
        });

        var parsed = Assert.IsType<AddRequest>(Parse(IpcProtocol.SerializeRequest(original)));

        Assert.Equal(original.Url, parsed.Url);
        Assert.Equal(original.Context.FileName, parsed.Context.FileName);
        Assert.Equal(10, parsed.Context.Size);
        Assert.Equal("1", parsed.Context.Headers["X-A"]);
        Assert.DoesNotContain('\n', IpcProtocol.SerializeRequest(original));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{}")]
    [InlineData("""{"type":5}""")]
    [InlineData("""{"type":"shutdown"}""")]
    [InlineData("""{"type":"activate","args":"--minimized"}""")]
    [InlineData("""{"type":"activate","args":[1]}""")]
    [InlineData("""{"type":"add"}""")]
    [InlineData("""{"type":"add","url":"javascript:alert(1)"}""")]
    [InlineData("""{"type":"add","url":"file:///etc/passwd"}""")]
    [InlineData("""{"type":"add","url":"https://example.com/a","finalUrl":"data:text/plain,x"}""")]
    [InlineData("""{"type":"add","url":"https://example.com/a","size":-1}""")]
    [InlineData("""{"type":"add","url":"https://example.com/a","size":1.5}""")]
    [InlineData("""{"type":"add","url":"https://example.com/a","size":"12"}""")]
    [InlineData("""{"type":"add","url":"https://example.com/a","fileName":7}""")]
    [InlineData("""{"type":"add","url":"https://example.com/a","referrer":"a\r\nInjected: 1"}""")]
    [InlineData("""{"type":"add","url":"https://example.com/a","headers":["X"]}""")]
    [InlineData("""{"type":"add","url":"https://example.com/a","headers":{"X-A":1}}""")]
    [InlineData("""{"type":"add","url":"https://example.com/a","headers":{"Bad Name":"1"}}""")]
    [InlineData("""{"type":"add","url":"https://example.com/a","headers":{"X-A":"line\nbreak"}}""")]
    public void Malformed_or_unsafe_requests_are_rejected(string line)
    {
        Assert.False(string.IsNullOrWhiteSpace(Reject(line)));
    }

    [Fact]
    public void Strings_over_their_limit_are_rejected()
    {
        var name = new string('a', IpcProtocol.MaxFileNameLength + 1);
        Reject($$"""{"type":"add","url":"https://example.com/a","fileName":"{{name}}"}""");
    }

    [Fact]
    public void Too_many_headers_are_rejected()
    {
        var headers = string.Join(",", Enumerable.Range(0, IpcProtocol.MaxHeaders + 1).Select(i => $"\"X-{i}\":\"v\""));
        Reject("""{"type":"add","url":"https://example.com/a","headers":{""" + headers + "}}");
    }

    [Fact]
    public void Response_round_trip()
    {
        Assert.Equal(IpcResponse.Success, IpcProtocol.ParseResponse(IpcProtocol.SerializeResponse(IpcResponse.Success)));
        Assert.Equal(IpcResponse.Failure("nope"), IpcProtocol.ParseResponse(IpcProtocol.SerializeResponse(IpcResponse.Failure("nope"))));
        Assert.Throws<InvalidDataException>(() => IpcProtocol.ParseResponse("{}"));
    }

    [Fact]
    public void Ping_and_config_requests_round_trip()
    {
        Assert.IsType<PingRequest>(Parse("""{"type":"ping"}"""));
        Assert.IsType<ConfigRequest>(Parse("""{"type":"config","extra":1}"""));
        Assert.IsType<PingRequest>(Parse(IpcProtocol.SerializeRequest(new PingRequest())));
        Assert.IsType<ConfigRequest>(Parse(IpcProtocol.SerializeRequest(new ConfigRequest())));
    }

    [Fact]
    public void Config_response_round_trip()
    {
        var response = new IpcResponse(true, Config: new CaptureConfig(["zip", "7z"], 512));

        var line = IpcProtocol.SerializeResponse(response);
        var parsed = IpcProtocol.ParseResponse(line);

        Assert.Equal("""{"ok":true,"captureExtensions":["zip","7z"],"minSizeKiB":512}""", line);
        Assert.True(parsed.Ok);
        Assert.Equal(["zip", "7z"], parsed.Config!.Extensions);
        Assert.Equal(512, parsed.Config.MinSizeKiB);
        Assert.Null(IpcProtocol.ParseResponse("""{"ok":true}""").Config);
    }

    [Theory]
    [InlineData("""{"ok":true,"captureExtensions":["zip"]}""")]
    [InlineData("""{"ok":true,"minSizeKiB":1}""")]
    [InlineData("""{"ok":true,"captureExtensions":"zip","minSizeKiB":1}""")]
    [InlineData("""{"ok":true,"captureExtensions":[1],"minSizeKiB":1}""")]
    [InlineData("""{"ok":true,"captureExtensions":["z.ip"],"minSizeKiB":1}""")]
    [InlineData("""{"ok":true,"captureExtensions":[""],"minSizeKiB":1}""")]
    [InlineData("""{"ok":true,"captureExtensions":["zip"],"minSizeKiB":-1}""")]
    [InlineData("""{"ok":true,"captureExtensions":["zip"],"minSizeKiB":1.5}""")]
    [InlineData("""{"ok":true,"captureExtensions":["abcdefghijklmnopq"],"minSizeKiB":1}""")]
    public void Malformed_capture_rules_in_a_response_are_rejected(string line)
    {
        Assert.Throws<InvalidDataException>(() => IpcProtocol.ParseResponse(line));
    }

    [Fact]
    public async Task Read_line_stops_at_the_newline_and_at_the_limit()
    {
        var ct = TestContext.Current.CancellationToken;

        var (line, tooLong) = await IpcProtocol.ReadLineAsync(new MemoryStream("hello\r\nrest"u8.ToArray()), 100, ct);
        Assert.Equal(("hello", false), (line, tooLong));

        (line, tooLong) = await IpcProtocol.ReadLineAsync(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 101) + "\n")), 100, ct);
        Assert.True(tooLong);

        (line, _) = await IpcProtocol.ReadLineAsync(new MemoryStream(), 100, ct);
        Assert.Null(line);

        await Assert.ThrowsAsync<InvalidDataException>(() => IpcProtocol.ReadLineAsync(new MemoryStream([0xC3, 0x28, 0x0A]), 100, ct));
    }
}
