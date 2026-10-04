using System.Net;
using System.Text.Json;
using System.Xml;
using Colibri.Core.Media;
using Colibri.Core.Services;

namespace Colibri.Platform.Media;

internal static class MediaManifestGuard
{
    public static async Task CheckAsync(string metadata, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(metadata, new() { MaxDepth = 32 });
        if (!document.RootElement.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Array) return;
        var manifests = new HashSet<string>();
        foreach (var format in formats.EnumerateArray()) {
            if (!format.TryGetProperty("protocol", out var protocol) || protocol.ValueKind != JsonValueKind.String) continue;
            var p = protocol.GetString();
            if (p is not ("m3u8" or "m3u8_native" or "dash" or "http_dash_segments")) continue;
            var value = format.TryGetProperty("manifest_url", out var manifest) && manifest.ValueKind == JsonValueKind.String ? manifest.GetString() :
                format.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString() : null;
            if (value is null || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new MediaHelperException("This media manifest cannot be verified as unprotected.");
            manifests.Add(uri.AbsoluteUri);
        }
        if (manifests.Count > 32) throw new MediaHelperException("Too many media manifests to verify safely.");
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        var seen = new HashSet<string>();
        foreach (var uri in manifests) await InspectAsync(client, new(uri), seen, 0, ct);
    }
    private static async Task InspectAsync(HttpClient client, Uri uri, HashSet<string> seen, int depth, CancellationToken ct)
    {
        if (!seen.Add(uri.AbsoluteUri)) return;
        if (seen.Count > 64 || depth > 3) throw new MediaHelperException("Media manifest nesting exceeds its supported limit.");
        for (var redirects = 0; ; redirects++) {
            if (!UrlPolicy.TryValidate(uri.AbsoluteUri, out _, out string? _) || uri.Scheme is not ("http" or "https")) throw new MediaHelperException("Invalid media manifest URL.");
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location) {
                if (redirects >= 5) throw new MediaHelperException("Media manifest redirects exceed the supported limit.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location); continue;
            }
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new MediaHelperException("The media manifest could not be verified.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct); using var buffer = new MemoryStream(); var bytes = new byte[4096]; int read;
            while ((read = await MediaSnapshotDownloader.ReadChunkAsync(stream, bytes, ct)) > 0) { if (buffer.Length + read > 2 * 1024 * 1024) throw new MediaHelperException("The media manifest exceeds its supported limit."); buffer.Write(bytes, 0, read); }
            var text = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            if (text.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal)) {
                foreach (var line in text.Split('\n').Select(s => s.Trim())) {
                    if (line.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.Ordinal) || line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal) && !line.Equals("#EXT-X-KEY:METHOD=NONE", StringComparison.Ordinal))
                        throw new MediaHelperException("Encrypted or protected media streams are unsupported.");
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    if (line.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)) await InspectAsync(client, new(uri, line), seen, depth + 1, ct);
                }
            }
            else {
                try {
                    using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
                    var rootSeen = false;
                    while (reader.Read()) if (reader.NodeType == XmlNodeType.Element) {
                        if (!rootSeen) { rootSeen = true; if (reader.LocalName != "MPD") throw new MediaHelperException("This media manifest format is unsupported."); }
                        if (reader.LocalName == "ContentProtection") throw new MediaHelperException("Encrypted or DRM media streams are unsupported.");
                    }
                    if (!rootSeen) throw new MediaHelperException("The media manifest is empty.");
                }
                catch (XmlException) { throw new MediaHelperException("The media manifest could not be verified."); }
            }
            return;
        }
    }
}
