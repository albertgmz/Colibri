using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Colibri.Core.Updates;

namespace Colibri.Platform.Updates;

/// <summary>Anonymous GitHub feed. No download credentials or browser cookies enter this client.</summary>
public sealed class GitHubReleaseUpdateService : IReleaseUpdateService, IDisposable
{
    private const string Repository = "albertgmz/Colibri";
    private const long PackageLimit = 512L * 1024 * 1024;
    private const int MetadataLimit = 1024 * 1024;
    private readonly HttpClient _http;
    private readonly string _cacheDirectory;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _checkGate = new(1);
    private readonly Func<VerifiedUpdatePackage, CancellationToken, Task>? _installer;
    private DateTimeOffset _nextCheck;
    private ReleaseUpdate? _cached;
    private string? _etag;
    private bool _hasChecked;
    private bool _hasValidatedCache;

    public GitHubReleaseUpdateService(HttpClient http, string currentVersion, UpdatePackageKind packageKind,
        string cacheDirectory, TimeProvider? time = null,
        Func<VerifiedUpdatePackage, CancellationToken, Task>? installer = null)
    {
        _http = http;
        CurrentVersion = ParseVersion(currentVersion).ToString(3);
        PackageKind = packageKind;
        _cacheDirectory = cacheDirectory;
        _time = time ?? TimeProvider.System;
        _installer = installer;
    }

    public string CurrentVersion { get; }
    public UpdatePackageKind PackageKind { get; }

    public static HttpClient CreateClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false, Credentials = null
    }) { Timeout = Timeout.InfiniteTimeSpan };

    internal static Version ParseVersion(string value)
    {
        if (!Regex.IsMatch(value, @"\A(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)\z") ||
            !Version.TryParse(value, out var version) || version.Major > 65534 || version.Minor > 65534 || version.Build > 65534)
            throw new InvalidDataException("Invalid release version.");
        return version;
    }

    public async Task<ReleaseUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        await _checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_time.GetUtcNow() < _nextCheck)
            {
                if (_hasChecked) return _cached;
                throw new UpdateFeedUnavailableException("Update feed is temporarily unavailable.");
            }
            _nextCheck = _time.GetUtcNow().AddSeconds(30);
            var hadValidatedMetadata = _hasValidatedCache;
            _hasChecked = false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = Request(new Uri($"https://api.github.com/repos/{Repository}/releases/latest"));
            if (_etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified && hadValidatedMetadata) { _hasChecked = true; return _cached; }
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                _hasChecked = false;
                var retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(5);
                _nextCheck = _time.GetUtcNow().Add(TimeSpan.FromSeconds(Math.Clamp(retry.TotalSeconds, 30, 3600)));
                throw new UpdateFeedUnavailableException("No public release is available, or GitHub temporarily limited requests.");
            }
            response.EnsureSuccessStatusCode();
            var bytes = await ReadBoundedAsync(response.Content, MetadataLimit, deadline.Token).ConfigureAwait(false);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var update = ParseRelease(json.RootElement, CurrentVersion);
            _cached = update;
            _etag = response.Headers.ETag?.ToString();
            _hasChecked = true;
            _hasValidatedCache = true;
            return update;
        }
        finally { _checkGate.Release(); }
    }

    internal static ReleaseUpdate? ParseRelease(JsonElement release, string installed)
    {
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("Expected a stable published release.");
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!tag.StartsWith('v')) throw new InvalidDataException("Invalid release tag.");
        var version = ParseVersion(tag[1..]);
        if (release.GetProperty("html_url").GetString() != $"https://github.com/{Repository}/releases/tag/{tag}")
            throw new InvalidDataException("Unexpected release repository.");
        if (version <= ParseVersion(installed)) return null;
        var assets = release.GetProperty("assets").EnumerateArray().ToArray();
        ReleasePackage Package(string suffix)
        {
            var name = $"Colibri-{version.ToString(3)}-win-x64-{suffix}";
            var matches = assets.Where(asset => asset.GetProperty("name").GetString() == name).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Release package is missing or duplicated.");
            var asset = matches[0];
            var url = $"https://github.com/{Repository}/releases/download/{tag}/{name}";
            var size = asset.GetProperty("size").GetInt64();
            var digest = asset.GetProperty("digest").GetString() ?? "";
            if (asset.GetProperty("state").GetString() != "uploaded" || size <= 0 || size > PackageLimit ||
                asset.GetProperty("browser_download_url").GetString() != url ||
                !Regex.IsMatch(digest, @"\Asha256:[A-Fa-f0-9]{64}\z"))
                throw new InvalidDataException("Invalid release package metadata.");
            return new ReleasePackage(name, new Uri(url), size, digest[7..].ToLowerInvariant());
        }
        return new ReleaseUpdate(version.ToString(3), Package("portable.zip"), Package("setup.exe"));
    }

    public async Task<VerifiedUpdatePackage> DownloadAsync(ReleaseUpdate update, CancellationToken cancellationToken)
    {
        if (PackageKind == UpdatePackageKind.None) throw new NotSupportedException("Windows packages are unavailable on this platform.");
        // Only an instance obtained from this client's validated metadata can be downloaded.
        if (!ReferenceEquals(update, _cached)) throw new InvalidDataException("Check the update feed before downloading.");
        var package = PackageKind == UpdatePackageKind.Installer ? update.Installer : update.Portable;
        Directory.CreateDirectory(_cacheDirectory);
        var folder = Path.Combine(_cacheDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var partial = Path.Combine(folder, package.Name + ".part");
        var final = Path.Combine(folder, package.Name);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(20));
            using var response = await DownloadResponseAsync(package.Url, deadline.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } declared && declared != package.Size)
                throw new InvalidDataException("Package size disagrees with the release feed.");
            await using (var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
                {
                    total += count;
                    if (total > package.Size) throw new InvalidDataException("Package exceeds its declared size.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                }
                if (total != package.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Package integrity verification failed.");
                await output.FlushAsync(deadline.Token).ConfigureAwait(false);
            }
            File.Move(partial, final, false);
            return new VerifiedUpdatePackage(final, package);
        }
        catch
        {
            if (File.Exists(partial)) File.Delete(partial);
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            throw;
        }
    }

    private async Task<HttpResponseMessage> DownloadResponseAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                (uri.Host != "github.com" && uri.Host != "release-assets.githubusercontent.com" && uri.Host != "objects.githubusercontent.com"))
                throw new InvalidDataException("Untrusted package redirect.");
            using var request = Request(uri);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidDataException("Missing package redirect destination.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("Too many package redirects.");
    }

    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("Release metadata is too large.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("Release metadata is too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static HttpRequestMessage Request(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Colibri-Updater/2");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        return request;
    }

    public async Task PrepareInstallAfterExitAsync(VerifiedUpdatePackage package, CancellationToken cancellationToken)
    {
        if (PackageKind != UpdatePackageKind.Installer || _installer is null || _cached is null || package.Source != _cached.Installer)
            throw new InvalidOperationException("Only an installed Windows app can launch its verified update installer.");
        await VerifyFileAsync(package, cancellationToken).ConfigureAwait(false);
        await _installer(package, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task VerifyFileAsync(VerifiedUpdatePackage package, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(package.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (file.Length != package.Source.Size ||
            !Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false)).Equals(package.Source.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cached update package changed; download it again.");
    }

    public void Dispose() { _http.Dispose(); _checkGate.Dispose(); }
}
