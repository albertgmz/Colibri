using Colibri.Core.Services;

namespace Colibri.Core.Tests.Services;

public class UrlPolicyTests
{
    [Theory]
    [InlineData("http://example.com/file.zip")]
    [InlineData("https://example.com/file.zip?x=1#y")]
    [InlineData("HTTPS://EXAMPLE.COM/FILE.ZIP")]
    [InlineData("ftp://ftp.example.com/pub/file.iso")]
    [InlineData("https://user:pass@example.com:8443/a")]
    [InlineData("http://192.168.1.10/file.bin")]
    [InlineData("http://[::1]:8080/file.bin")]
    [InlineData("  https://example.com/padded  ")]
    public void Accepts_http_https_and_ftp(string url)
    {
        var ok = UrlPolicy.TryValidate(url, out var uri, out var error);

        Assert.True(ok);
        Assert.NotNull(uri);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("blob:https://example.com/550e8400-e29b-41d4-a716-446655440000")]
    [InlineData("data:text/plain;base64,SGVsbG8=")]
    [InlineData("file:///etc/passwd")]
    [InlineData("file://C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("chrome-extension://abc/page.html")]
    [InlineData("ws://example.com/socket")]
    [InlineData("/relative/path.zip")]
    [InlineData("relative/path.zip")]
    [InlineData("C:\\Windows\\notepad.exe")]
    [InlineData("example.com/file.zip")]
    [InlineData("https://")]
    [InlineData("http:///no-host")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_everything_else(string? url)
    {
        var ok = UrlPolicy.TryValidate(url, out var uri, out var error);

        Assert.False(ok);
        Assert.Null(uri);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("https://example.com/a b.zip")]
    [InlineData("https://example.com/a\tb.zip")]
    [InlineData("https://exa\tmple.com/file.zip")]
    [InlineData("https://example.com/a\r\nb.zip")]
    [InlineData("https://example.com/a\nInjected: header")]
    [InlineData("https://example.com/a\u0000.zip")]
    [InlineData("https://example.com/a\u007F.zip")]
    [InlineData("https://example.com/a\u001F.zip")]
    public void Rejects_whitespace_and_control_characters_inside_the_url(string url)
    {
        Assert.False(UrlPolicy.TryValidate(url, out var uri, out var error));
        Assert.Null(uri);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("https://user:secret@example.com:8443/a/b.zip?token=abc#frag", "https://example.com:8443/a/b.zip")]
    [InlineData("https://example.com/files/x.iso?sig=123", "https://example.com/files/x.iso")]
    [InlineData("ftp://anonymous:me%40example.com@ftp.example.com/pub/f.tar.gz", "ftp://ftp.example.com/pub/f.tar.gz")]
    [InlineData("http://example.com", "http://example.com/")]
    [InlineData("https://example.com:443/default-port", "https://example.com/default-port")]
    [InlineData("http://[::1]:8080/file.bin?k=v", "http://[::1]:8080/file.bin")]
    public void Redact_drops_user_info_query_and_fragment(string url, string expected)
    {
        Assert.Equal(expected, UrlPolicy.Redact(new Uri(url)));
    }

    [Fact]
    public void Accepts_url_at_the_length_limit()
    {
        var prefix = "https://example.com/";
        var url = prefix + new string('a', UrlPolicy.MaxLength - prefix.Length);

        Assert.True(UrlPolicy.TryValidate(url, out _, out _));
    }

    [Fact]
    public void Rejects_url_over_the_length_limit()
    {
        var prefix = "https://example.com/";
        var url = prefix + new string('a', UrlPolicy.MaxLength - prefix.Length + 1);

        Assert.False(UrlPolicy.TryValidate(url, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("https://example.com/a", UrlValidationError.None)]
    [InlineData("   ", UrlValidationError.Empty)]
    [InlineData("https://example.com/a b", UrlValidationError.InvalidCharacters)]
    [InlineData("relative/path.zip", UrlValidationError.NotAbsolute)]
    [InlineData("javascript:alert(1)", UrlValidationError.UnsupportedScheme)]
    public void Reports_the_reason_for_a_rejection(string url, UrlValidationError expected)
    {
        var ok = UrlPolicy.TryValidateWithReason(url, out _, out var reason);

        Assert.Equal(expected == UrlValidationError.None, ok);
        Assert.Equal(expected, reason);
    }

    [Fact]
    public void Reports_too_long_urls()
    {
        var url = "https://example.com/" + new string('a', UrlPolicy.MaxLength);

        Assert.False(UrlPolicy.TryValidateWithReason(url, out _, out var reason));
        Assert.Equal(UrlValidationError.TooLong, reason);
    }
}
