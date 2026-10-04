using Colibri.Core.Media;
namespace Colibri.Core.Tests.Media;
public class MediaSegmentPlanTests
{
    [Fact] public void HlsSnapshotRejectsEncryptionAndLiveAndResolvesSegments() {
        var uri = new Uri("https://example.com/path/index.m3u8?token=secret");
        var plan = MediaSegmentPlans.ParseHls(uri, "#EXTM3U\n#EXTINF:1,\nfirst.ts\n#EXT-X-ENDLIST\n");
        Assert.Equal("https://example.com/path/first.ts", Assert.Single(plan.Segments).AbsoluteUri);
        Assert.DoesNotContain("secret", plan.ToString());
        Assert.Throws<MediaHelperException>(() => MediaSegmentPlans.ParseHls(uri, "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key\"\nx.ts\n#EXT-X-ENDLIST"));
        Assert.Throws<MediaHelperException>(() => MediaSegmentPlans.ParseHls(uri, "#EXTM3U\nx.ts"));
        Assert.Throws<MediaHelperException>(() => MediaSegmentPlans.ParseHls(uri, "#EXTM3U\nfile:///etc/passwd\n#EXT-X-ENDLIST"));
    }
    [Fact] public void DashNeedsExplicitHttpFragmentsAndManifestProtectionCheck() {
        var json = """{"formats":[{"format_id":"v","protocol":"http_dash_segments","url":"https://example.com/init.mp4","manifest_url":"https://example.com/index.mpd","fragment_base_url":"https://example.com/chunks/","fragments":[{"url":"init.mp4"},{"url":"1.m4s"}]}]}""";
        var plan = MediaSegmentPlans.ParseFormat(json, "v");
        Assert.Equal(2, plan.Segments.Count);
        Assert.Equal("https://example.com/chunks/1.m4s", plan.Segments[1].AbsoluteUri);
        Assert.Throws<MediaHelperException>(() => MediaSegmentPlans.ParseFormat(json.Replace("1.m4s", "file:///etc/passwd"), "v"));
        Assert.Throws<MediaHelperException>(() => MediaSegmentPlans.ParseFormat(json.Replace("\"fragments\"", "\"implicit_segments\""), "v"));
    }
}
