using Colibri.Core.Models;

namespace Colibri.Core.Tests.Models;

public class HttpHeadersTests
{
    [Fact]
    public void Copy_merges_names_differing_only_in_case_last_wins()
    {
        var source = new[]
        {
            KeyValuePair.Create("X-Token", "first"),
            KeyValuePair.Create("x-token", "second"),
        };

        var copy = HttpHeaders.Copy(source);

        Assert.Single(copy);
        Assert.Equal("second", copy["X-TOKEN"]);
    }

    [Fact]
    public void CopyValid_drops_invalid_names_and_values()
    {
        var source = new Dictionary<string, string>
        {
            ["X-Good"] = "ok",
            ["Bad Name"] = "x",
            ["X-Inject"] = "a\r\nSet-Cookie: evil=1",
            [""] = "empty name",
        };

        var copy = HttpHeaders.CopyValid(source);

        Assert.Equal("ok", Assert.Single(copy).Value);
    }

    [Theory]
    [InlineData("Accept")]
    [InlineData("X-Custom_Header.1")]
    [InlineData("!#$%&'*+-.^_`|~")]
    public void Valid_header_names(string name)
    {
        Assert.True(HttpHeaders.IsValidName(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Has Space")]
    [InlineData("Colon:")]
    [InlineData("New\nLine")]
    [InlineData("Quote\"")]
    [InlineData("(paren)")]
    [InlineData("Ünicode")]
    public void Invalid_header_names(string name)
    {
        Assert.False(HttpHeaders.IsValidName(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("text/html, application/json;q=0.9")]
    [InlineData("tab\tis allowed")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64)")]
    public void Valid_header_values(string value)
    {
        Assert.True(HttpHeaders.IsValidValue(value));
    }

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("carriage\rreturn")]
    [InlineData("nul\0char")]
    [InlineData("bell\u0007")]
    [InlineData("del\u007F")]
    public void Invalid_header_values(string value)
    {
        Assert.False(HttpHeaders.IsValidValue(value));
    }

    [Theory]
    [InlineData("Range")]
    [InlineData("accept-encoding")]
    [InlineData("Content-Length")]
    [InlineData("Host")]
    [InlineData("Connection")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Upgrade")]
    [InlineData("TE")]
    [InlineData("Keep-Alive")]
    [InlineData("Proxy-Authorization")]
    [InlineData("proxy-connection")]
    [InlineData("Cookie")]
    [InlineData("Referer")]
    [InlineData("User-Agent")]
    [InlineData("Bad Name")]
    [InlineData("")]
    public void Headers_that_aria2_manages_or_that_have_their_own_field_are_not_forwarded(string name)
    {
        Assert.False(HttpHeaders.IsForwardable(name));
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Accept")]
    [InlineData("Accept-Language")]
    [InlineData("X-Requested-With")]
    [InlineData("Sec-Fetch-Mode")]
    public void Ordinary_request_headers_are_forwarded(string name)
    {
        Assert.True(HttpHeaders.IsForwardable(name));
    }
}
