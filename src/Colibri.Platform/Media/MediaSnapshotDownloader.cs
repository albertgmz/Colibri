using System.Net;
using System.Diagnostics;
using System.Text;
using Colibri.Core.Media;
using Colibri.Core.Models;
namespace Colibri.Platform.Media;

internal sealed class MediaSnapshotDownloader(LinkContext context, Func<long> rateLimit) : IDisposable
{
    private readonly HttpClient _client = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    public async Task<(string Text, Uri FinalUri)> ReadManifestAsync(Uri uri, CancellationToken ct)
    {
        using var response = await RequestAsync(uri, ct);
        if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new MediaHelperException("Media manifest exceeds the supported limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct); using var buffer = new MemoryStream(); var bytes = new byte[4096]; int read;
        while ((read = await ReadChunkAsync(stream, bytes, ct)) > 0) { if (buffer.Length + read > 2 * 1024 * 1024) throw new MediaHelperException("Media manifest exceeds the supported limit."); await buffer.WriteAsync(bytes.AsMemory(0, read), ct); }
        return (Encoding.UTF8.GetString(buffer.ToArray()), response.RequestMessage!.RequestUri!);
    }
    public async Task<long> DownloadAsync(MediaSourcePlan plan, string output, Action<long, long> progress, CancellationToken ct)
    {
        MediaPathSafety.RequireNoLinks(output);
        await using var file = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        var bytes = new byte[64 * 1024]; long completed = 0, total = 0; var elapsed = Stopwatch.StartNew();
        foreach (var segment in plan.Segments) {
            using var response = await RequestAsync(segment, ct);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is "application/vnd.apple.mpegurl" or "application/x-mpegurl" or "application/dash+xml") throw new MediaHelperException("An implicit remote manifest cannot be downloaded as a direct media segment.");
            total += Math.Max(0, response.Content.Headers.ContentLength ?? 0);
            await using var stream = await response.Content.ReadAsStreamAsync(ct); int read;
            while ((read = await ReadChunkAsync(stream, bytes, ct)) > 0) {
                await file.WriteAsync(bytes.AsMemory(0, read), ct); completed += read;
                var limit = rateLimit();
                if (limit > 0) {
                    var delay = TimeSpan.FromSeconds((double)completed / limit) - elapsed.Elapsed;
                    while (delay > TimeSpan.Zero) { await Task.Delay(delay > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay, ct); limit = rateLimit(); if (limit == 0) break; delay = TimeSpan.FromSeconds((double)completed / Math.Max(1, limit)) - elapsed.Elapsed; }
                }
                progress(completed, plan.Segments.Count == 1 ? total : 0);
            }
        }
        await file.FlushAsync(ct); return completed;
    }
    private async Task<HttpResponseMessage> RequestAsync(Uri source, CancellationToken ct)
    {
        var uri = source;
        for (var redirects = 0; ; redirects++) {
            MediaSegmentPlans.RequireHttp(uri.AbsoluteUri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            foreach (var header in context.Headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (HttpHeaders.IsValidValue(context.UserAgent)) request.Headers.TryAddWithoutValidation("User-Agent", context.UserAgent);
            if (Uri.TryCreate(context.Referrer, UriKind.Absolute, out var referer) && referer.Scheme is "http" or "https") request.Headers.Referrer = new(referer.GetLeftPart(UriPartial.Authority) + "/");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location) {
                response.Dispose(); if (redirects >= 5) throw new MediaHelperException("Media redirect limit exceeded.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location); continue;
            }
            if (response.StatusCode != HttpStatusCode.OK) { response.Dispose(); throw new MediaHelperException("The media segment could not be downloaded."); }
            return response;
        }
    }
    internal static async Task<int> ReadChunkAsync(Stream stream, Memory<byte> bytes, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await stream.ReadAsync(bytes, timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new MediaHelperException("A media response stopped producing bytes."); }
    }
    public void Dispose() => _client.Dispose();
}
