using System.Text.Json;
using System.Text.RegularExpressions;
using Colibri.Core.Models;
using Colibri.Core.Network;

namespace Colibri.Core.Media;

public sealed class MediaHelperException(string message) : Exception(message);
public sealed record MediaSelection(string FormatId, string? AudioFormatId, string OutputExtension)
{
    public bool RequiresMux => AudioFormatId is not null;
    public void Validate()
    {
        if (!ValidId(FormatId) || AudioFormatId is not null && (!ValidId(AudioFormatId) || FormatId == AudioFormatId) ||
            !MediaMetadataParser.Extensions.Contains(OutputExtension) || RequiresMux && OutputExtension != "mkv")
            throw new MediaHelperException("Invalid media selection.");
    }
    internal static bool ValidId(string value) => Regex.IsMatch(value ?? "", "^[a-zA-Z0-9_.-]{1,128}$", RegexOptions.CultureInvariant);
}
public sealed record MediaFormat(string Id, string Extension, string VideoCodec, string AudioCodec, int? Height, long? Size)
{
    public bool HasVideo => VideoCodec != "none";
    public bool HasAudio => AudioCodec != "none";
    public string Label => $"{Id} · {Extension} · {(Height is { } h ? h + "p" : VideoCodec == "none" ? "audio" : "video")} · {AudioCodec}";
}
public sealed record MediaMetadata(string Title, IReadOnlyList<MediaFormat> Formats);
public sealed record MediaToolInfo(string YtDlpVersion, string YtDlpLicense, string? FfmpegVersion, string? FfmpegLicense);
public interface IMediaHelper
{
    Task<MediaToolInfo> ValidateToolsAsync(CancellationToken ct);
    Task<MediaMetadata> InspectAsync(Uri source, LinkContext context, DownloadNetworkPolicy? network, CancellationToken ct);
}
public static class MediaPolicy
{
    public static void Validate(DownloadNetworkPolicy? network, LinkContext context)
    {
        if (!string.IsNullOrWhiteSpace(network?.RequiredInterfaceId) || network?.Proxy is not null)
            throw new MediaHelperException("The configured media helper cannot enforce adapter or proxy policies.");
        if (!string.IsNullOrWhiteSpace(context.Cookies) || context.Headers.Any(h => !h.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase) && !h.Key.Equals("Accept-Language", StringComparison.OrdinalIgnoreCase)))
            throw new MediaHelperException("Authenticated or custom-header media extraction is unsupported; use a direct download for that request context.");
        if (context.RequestMethod != "GET") throw new MediaHelperException("Only GET media requests are supported.");
    }
}
public static class MediaMetadataParser
{
    public const int MaxCharacters = 4 * 1024 * 1024;
    public static IReadOnlySet<string> Extensions { get; } = new HashSet<string>(["mp4", "webm", "mkv", "m4a", "mp3", "ogg", "opus", "flac", "wav", "aac", "mov", "m4v", "ts"]);
    public static MediaMetadata Parse(string json)
    {
        try {
            if (json.Length > MaxCharacters) throw new MediaHelperException("Media metadata exceeds the supported limit.");
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 32 });
            var root = document.RootElement;
            string Text(JsonElement e, string key, int max = 128) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s && s.Length <= max && !s.Any(char.IsControl) ? s : "";
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("entries", out _) || Text(root, "_type") is "playlist" or "multi_video" ||
                root.TryGetProperty("has_drm", out var drm) && drm.ValueKind == JsonValueKind.True || root.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True)
                throw new MediaHelperException("Playlists, live media and DRM media are unsupported.");
            if (!root.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Array || formats.GetArrayLength() > 1000)
                throw new MediaHelperException("Media formats are missing or exceed the supported limit.");
            var result = new List<MediaFormat>();
            foreach (var f in formats.EnumerateArray()) {
                if (f.ValueKind != JsonValueKind.Object || f.TryGetProperty("has_drm", out drm) && drm.ValueKind == JsonValueKind.True) continue;
                var id = Text(f, "format_id"); var ext = Text(f, "ext").ToLowerInvariant();
                var video = Text(f, "vcodec"); var audio = Text(f, "acodec");
                var protocol = Text(f, "protocol");
                if (!MediaSelection.ValidId(id) || !Extensions.Contains(ext) || video.Length == 0 || audio.Length == 0 || video == "none" && audio == "none" ||
                    protocol.Length > 0 && protocol is not ("http" or "https" or "m3u8_native" or "m3u8" or "http_dash_segments" or "dash")) continue;
                int? height = f.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number && h.TryGetInt32(out var hv) && hv is > 0 and <= 16384 ? hv : null;
                long? size = f.TryGetProperty("filesize", out var b) && b.ValueKind == JsonValueKind.Number && b.TryGetInt64(out var bv) && bv > 0 ? bv : null;
                if (!result.Any(r => r.Id == id)) result.Add(new(id, ext, video, audio, height, size));
            }
            if (result.Count == 0) throw new MediaHelperException("No supported playable media formats were found.");
            var title = Text(root, "title", 1024);
            return new(title.Length > 0 ? title : "media", result);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) {
            throw new MediaHelperException("The media helper returned invalid metadata.");
        }
    }
}
