using Colibri.Core.Models;
using Colibri.Core.Services;

namespace Colibri.Core.Tests.Services;

public class DirectLinkResolverTests
{
    private readonly DirectLinkResolver _resolver = new();

    [Fact]
    public void Has_the_lowest_priority()
    {
        Assert.Equal(int.MinValue, _resolver.Priority);
    }

    [Theory]
    [InlineData("https://example.com/files/setup.exe", "setup.exe")]
    [InlineData("https://example.com/files/my%20file%20%281%29.zip?token=abc#part", "my file (1).zip")]
    [InlineData("https://example.com/%E6%97%A5%E6%9C%AC.pdf", "日本.pdf")]
    [InlineData("ftp://ftp.example.com/pub/archive.tar.gz", "archive.tar.gz")]
    public async Task Takes_the_file_name_from_the_last_path_segment(string url, string expected)
    {
        var request = await ResolveSingle(new Uri(url), LinkContext.Empty);

        Assert.Equal(expected, request.SuggestedFileName);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("https://example.com/")]
    [InlineData("https://example.com/folder/")]
    public async Task No_file_name_when_the_url_has_no_last_segment(string url)
    {
        var request = await ResolveSingle(new Uri(url), LinkContext.Empty);

        Assert.Null(request.SuggestedFileName);
    }

    [Fact]
    public async Task File_name_from_context_wins_over_the_url()
    {
        var context = new LinkContext { FileName = "real-name.iso" };

        var request = await ResolveSingle(new Uri("https://example.com/download.php?id=7"), context);

        Assert.Equal("real-name.iso", request.SuggestedFileName);
    }

    [Fact]
    public async Task Copies_the_context_into_the_request()
    {
        var url = new Uri("https://example.com/a.zip");
        var context = new LinkContext
        {
            Referrer = "https://example.com/page",
            Cookies = "session=1",
            UserAgent = "TestAgent/1.0",
            Size = 1234,
            MimeType = "application/zip",
            Headers = new Dictionary<string, string> { ["X-Token"] = "secret" },
        };

        var request = await ResolveSingle(url, context);

        Assert.Equal(url, request.Uri);
        Assert.Equal("https://example.com/page", request.Referrer);
        Assert.Equal("session=1", request.Cookies);
        Assert.Equal("TestAgent/1.0", request.UserAgent);
        Assert.Equal(1234, request.Size);
        Assert.Equal("application/zip", request.MimeType);
        Assert.Equal("secret", request.Headers["x-token"]);
    }

    [Fact]
    public async Task Drops_invalid_headers_and_values()
    {
        var context = new LinkContext
        {
            Referrer = "https://example.com/\r\nX-Evil: 1",
            Cookies = "a=1\nb=2",
            UserAgent = "Agent\0",
            Headers = new Dictionary<string, string>
            {
                ["X-Good"] = "ok",
                ["Bad Name"] = "x",
                ["X-Inject"] = "a\r\nSet-Cookie: evil=1",
            },
        };

        var request = await ResolveSingle(new Uri("https://example.com/a.zip"), context);

        Assert.Null(request.Referrer);
        Assert.Null(request.Cookies);
        Assert.Null(request.UserAgent);
        Assert.Equal("X-Good", Assert.Single(request.Headers).Key);
    }

    private async Task<DownloadRequest> ResolveSingle(Uri url, LinkContext context)
    {
        var result = await _resolver.ResolveAsync(url, context, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        return Assert.Single(result);
    }
}
