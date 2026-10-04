using Colibri.Core.Media;
using Colibri.Core.Network;
using Colibri.Core.Models;
namespace Colibri.Core.Tests.Media;
public class MediaPolicyTests
{
    [Fact] public void MetadataDropsSecretUrlsAndRejectsDrmPlaylistsAndOversize() {
        var metadata = MediaMetadataParser.Parse("""{"title":"Sample","formats":[{"format_id":"v1","ext":"mp4","vcodec":"h264","acodec":"none","height":720,"url":"https://example.com/?token=secret"},{"format_id":"a1","ext":"m4a","vcodec":"none","acodec":"aac"}]}""");
        Assert.Equal("v1", metadata.Formats[0].Id);
        Assert.DoesNotContain("token", metadata.ToString());
        Assert.Throws<MediaHelperException>(() => MediaMetadataParser.Parse("""{"has_drm":true,"formats":[]}"""));
        Assert.Throws<MediaHelperException>(() => MediaMetadataParser.Parse("""{"_type":"playlist","entries":[]}"""));
        Assert.Throws<MediaHelperException>(() => MediaMetadataParser.Parse(new string('x', MediaMetadataParser.MaxCharacters + 1)));
    }
    [Fact] public void NullableAudioDimensionsAndUnknownSizesRemainInspectable() {
        var metadata = MediaMetadataParser.Parse("""{"title":"Audio","formats":[{"format_id":"audio","ext":"m4a","vcodec":"none","acodec":"aac","height":null,"filesize":null},{"format_id":"video","ext":"mp4","vcodec":"h264","acodec":"aac","height":720,"filesize":1024}]}""");
        Assert.Null(metadata.Formats[0].Height); Assert.Null(metadata.Formats[0].Size);
        Assert.Equal(720, metadata.Formats[1].Height); Assert.Equal(1024, metadata.Formats[1].Size);
    }
    [Fact] public void SelectionCannotInjectFormatsOrOutputPaths() {
        Assert.Throws<MediaHelperException>(() => new MediaSelection("v1+exec", null, "mp4").Validate());
        Assert.Throws<MediaHelperException>(() => new MediaSelection("v1", null, "../exe").Validate());
        Assert.Throws<MediaHelperException>(() => new MediaSelection("v1", "v1", "mkv").Validate());
        new MediaSelection("v1", "a1", "mkv").Validate();
    }
    [Fact] public void NetworkAndReplayRestrictionsApplyBeforeAnyToolStarts() {
        Assert.Throws<MediaHelperException>(() => MediaPolicy.Validate(new() { RequiredInterfaceId = "adapter" }, LinkContext.Empty));
        Assert.Throws<MediaHelperException>(() => MediaPolicy.Validate(new() { Proxy = new() { Endpoint = new("http://localhost:8080") } }, LinkContext.Empty));
        Assert.Throws<MediaHelperException>(() => MediaPolicy.Validate(null, new() { RequestMethod = "POST" }));
        MediaPolicy.Validate(null, LinkContext.Empty);
    }
}
