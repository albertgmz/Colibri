using Colibri.Platform.Media;
using Colibri.Core.Media;
using Colibri.Core.Models;
namespace Colibri.Platform.Tests.Media;
public class MediaConfigurationTests
{
    [Fact] public void StdinConfigurationQuotesTokensAndDropsReferrerQuery() {
        var configuration = YtDlpMediaHelper.BuildConfiguration(new("https://example.com/video?token=a'b"), new() { Referrer = "https://example.com/page?secret=x", UserAgent = "browser'agent" });
        Assert.DoesNotContain("secret", configuration);
        Assert.Contains("--referer 'https://example.com/'", configuration);
        Assert.Contains("'\\''", configuration);
        Assert.Throws<MediaHelperException>(() => YtDlpMediaHelper.BuildConfiguration(new("https://example.com/a"), new() { UserAgent = "browser\n--exec evil" }));
    }
}
