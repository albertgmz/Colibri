using System.Text.Json;
using System.Text.RegularExpressions;
using Colibri.Core.Services;
namespace Colibri.Core.Media;

/// <summary>Transient URL snapshots; never persisted with a download or included in diagnostic text.</summary>
public sealed record MediaSourcePlan(IReadOnlyList<Uri> Segments, bool NeedsRemux)
{
    public override string ToString() => $"MediaSourcePlan {{ segments = {Segments.Count}, localRemux = {NeedsRemux} }}";
}
public static class MediaSegmentPlans
{
    public const int MaxSegments = 10000;
    public static Uri RequireHttp(string url, Uri? baseUri = null)
    {
        if (!Uri.TryCreate(baseUri, url, out var uri) || uri.Scheme is not ("http" or "https") || !UrlPolicy.TryValidate(uri.AbsoluteUri, out _, out string? _))
            throw new MediaHelperException("Invalid media segment URL.");
        return uri;
    }
    public static MediaSourcePlan ParseHls(Uri source, string manifest)
    {
        if (manifest.Length > 2 * 1024 * 1024 || !manifest.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal)) throw new MediaHelperException("Invalid or oversized HLS playlist.");
        var lines = manifest.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        if (!lines.Contains("#EXT-X-ENDLIST") || lines.Any(l => l.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal))) throw new MediaHelperException("Only finite HLS media playlists are supported.");
        var segments = new List<Uri>(); var initialized = false;
        foreach (var line in lines) {
            if (line.StartsWith("#EXT-X-SESSION-KEY", StringComparison.Ordinal) || line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal) && line != "#EXT-X-KEY:METHOD=NONE")
                throw new MediaHelperException("Encrypted or protected HLS streams are unsupported.");
            if (line.StartsWith("#EXT-X-BYTERANGE", StringComparison.Ordinal) || line.StartsWith("#EXT-X-PART", StringComparison.Ordinal) || line.StartsWith("#EXT-X-DISCONTINUITY", StringComparison.Ordinal))
                throw new MediaHelperException("HLS byte ranges, partial streams and discontinuities are unsupported.");
            if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal)) {
                if (initialized || segments.Count != 0 || line.Contains("BYTERANGE", StringComparison.Ordinal)) throw new MediaHelperException("Unsupported HLS initialization map.");
                var match = Regex.Match(line, "^#EXT-X-MAP:URI=\"([^\"]+)\"$", RegexOptions.CultureInvariant);
                if (!match.Success) throw new MediaHelperException("Invalid HLS initialization map.");
                segments.Add(RequireHttp(match.Groups[1].Value, source)); initialized = true;
            }
            else if (!line.StartsWith('#')) segments.Add(RequireHttp(line, source));
            if (segments.Count > MaxSegments) throw new MediaHelperException("HLS segment count exceeds the supported limit.");
        }
        if (segments.Count <= (initialized ? 1 : 0)) throw new MediaHelperException("The HLS playlist contains no media segments.");
        return new(segments, true);
    }
    public static MediaSourcePlan ParseFormat(string metadata, string formatId)
    {
        try {
            if (metadata.Length > MediaMetadataParser.MaxCharacters) throw new MediaHelperException("Media metadata exceeds the supported limit.");
            using var document = JsonDocument.Parse(metadata, new() { MaxDepth = 32 });
            var format = document.RootElement.GetProperty("formats").EnumerateArray().FirstOrDefault(f => f.TryGetProperty("format_id", out var id) && id.GetString() == formatId);
            if (format.ValueKind != JsonValueKind.Object || format.TryGetProperty("has_drm", out var drm) && drm.ValueKind == JsonValueKind.True || format.TryGetProperty("hls_aes", out _)) throw new MediaHelperException("The selected media format is unavailable or protected.");
            var protocol = format.TryGetProperty("protocol", out var p) ? p.GetString() : "https";
            var source = RequireHttp(format.GetProperty("url").GetString()!);
            if (protocol is "http" or "https" or null or "") return new([source], false);
            if (protocol is "m3u8" or "m3u8_native") return new([source], true); // Fetch once, then replace with ParseHls snapshot.
            if (protocol is not ("dash" or "http_dash_segments") || !format.TryGetProperty("manifest_url", out var manifest) || !Uri.TryCreate(manifest.GetString(), UriKind.Absolute, out _) ||
                !format.TryGetProperty("fragments", out var fragments) || fragments.ValueKind != JsonValueKind.Array || fragments.GetArrayLength() is < 1 or > MaxSegments)
                throw new MediaHelperException("Only explicit finite DASH fragment plans are supported.");
            var baseUri = format.TryGetProperty("fragment_base_url", out var b) ? RequireHttp(b.GetString()!) : source;
            var urls = new List<Uri>();
            foreach (var fragment in fragments.EnumerateArray()) {
                if (fragment.ValueKind != JsonValueKind.Object || fragment.TryGetProperty("range", out _) || fragment.TryGetProperty("byte_range", out _) ||
                    !fragment.TryGetProperty("url", out var u) || u.ValueKind != JsonValueKind.String)
                    throw new MediaHelperException("Unsupported DASH fragment plan.");
                urls.Add(RequireHttp(u.GetString()!, baseUri));
            }
            return new(urls, true);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException) { throw new MediaHelperException("Invalid media segment metadata."); }
    }
}
