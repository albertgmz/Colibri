using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Colibri.Core.Updates;
using Colibri.Platform.Updates;

namespace Colibri.Platform.Tests;

public class ReleaseUpdateTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("verified package fixture");
    private static readonly string Digest = Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();

    private static object Asset(string suffix) => new
    {
        name = $"Colibri-2.0.9-win-x64-{suffix}", size = Payload.Length, state = "uploaded", digest = "sha256:" + Digest,
        browser_download_url = $"https://github.com/albertgmz/Colibri/releases/download/v2.0.9/Colibri-2.0.9-win-x64-{suffix}"
    };
    private static string Feed() => JsonSerializer.Serialize(new
    {
        tag_name = "v2.0.9", draft = false, prerelease = false,
        html_url = "https://github.com/albertgmz/Colibri/releases/tag/v2.0.9",
        assets = new[] { Asset("setup.exe"), Asset("portable.zip") }
    });
    private static HttpResponseMessage JsonResponse(string? json = null) => new(HttpStatusCode.OK) { Content = new StringContent(json ?? Feed()) };

    [Theory]
    [InlineData("2.0.0-beta")]
    [InlineData("02.0.0")]
    [InlineData("2.0")]
    [InlineData("2.0.65535")]
    [InlineData("2.0.0 ")]
    public void Noncanonical_versions_are_rejected(string version) => Assert.Throws<InvalidDataException>(() => GitHubReleaseUpdateService.ParseVersion(version));

    [Fact]
    public void Stable_feed_rejects_wrong_repository_missing_digest_and_draft()
    {
        foreach (var altered in new[] {
            Feed().Replace("albertgmz/Colibri", "other/Colibri"),
            Feed().Replace("sha256:" + Digest, ""),
            Feed().Replace("\"draft\":false", "\"draft\":true"),
            Feed().Replace("v2.0.9", "v02.0.9"),
            Feed().Replace("\"state\":\"uploaded\"", "\"state\":\"new\"") })
        {
            using var json = JsonDocument.Parse(altered);
            Assert.Throws<InvalidDataException>(() => GitHubReleaseUpdateService.ParseRelease(json.RootElement, "2.0.0"));
        }
        using var valid = JsonDocument.Parse(Feed());
        Assert.Null(GitHubReleaseUpdateService.ParseRelease(valid.RootElement, "2.0.9"));
        Assert.Null(GitHubReleaseUpdateService.ParseRelease(valid.RootElement, "2.1.0"));
        Assert.Equal("2.0.9", GitHubReleaseUpdateService.ParseRelease(valid.RootElement, "2.0.0")!.Version);
    }

    [Fact]
    public async Task Checks_share_validated_cache_and_send_ETag_after_throttle()
    {
        var clock = new Clock(); var calls = 0;
        using var scope = new ServiceScope(request =>
        {
            calls++;
            Assert.Equal("https://api.github.com/repos/albertgmz/Colibri/releases/latest", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("2026-03-10", request.Headers.GetValues("X-GitHub-Api-Version").Single());
            if (calls == 1) { var response = JsonResponse(); response.Headers.ETag = new EntityTagHeaderValue("\"release\""); return response; }
            Assert.Equal("\"release\"", request.Headers.IfNoneMatch.Single().ToString());
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        }, clock);
        var update = await scope.Service.CheckAsync(CancellationToken.None);
        Assert.Same(update, await scope.Service.CheckAsync(CancellationToken.None));
        Assert.Equal(1, calls);
        clock.Now += TimeSpan.FromHours(1);
        Assert.Same(update, await scope.Service.CheckAsync(CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Private_or_empty_feed_is_unavailable_and_not_current()
    {
        var calls = 0;
        using var scope = new ServiceScope(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.NotFound); });
        await Assert.ThrowsAsync<UpdateFeedUnavailableException>(() => scope.Service.CheckAsync(CancellationToken.None));
        await Assert.ThrowsAsync<UpdateFeedUnavailableException>(() => scope.Service.CheckAsync(CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Validated_ETag_cache_recovers_after_a_failed_check()
    {
        var clock = new Clock(); var calls = 0;
        using var scope = new ServiceScope(_ =>
        {
            calls++;
            if (calls == 1) { var response = JsonResponse(); response.Headers.ETag = new EntityTagHeaderValue("\"release\""); return response; }
            return new HttpResponseMessage(calls == 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NotModified);
        }, clock);
        var valid = await scope.Service.CheckAsync(CancellationToken.None);
        clock.Now += TimeSpan.FromHours(1);
        await Assert.ThrowsAsync<HttpRequestException>(() => scope.Service.CheckAsync(CancellationToken.None));
        clock.Now += TimeSpan.FromHours(1);
        Assert.Same(valid, await scope.Service.CheckAsync(CancellationToken.None));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Oversized_metadata_is_rejected_before_parsing()
    {
        using var scope = new ServiceScope(_ => JsonResponse(new string(' ', 1024 * 1024 + 1)));
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Service.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Download_stream_is_verified_and_changed_cache_is_rejected()
    {
        using var scope = new ServiceScope(request => request.RequestUri!.Host == "api.github.com"
            ? JsonResponse() : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
        var release = (await scope.Service.CheckAsync(CancellationToken.None))!;
        var package = await scope.Service.DownloadAsync(release, CancellationToken.None);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(package.Path, TestContext.Current.CancellationToken));
        Assert.EndsWith("Colibri-2.0.9-win-x64-portable.zip", package.Path);
        await GitHubReleaseUpdateService.VerifyFileAsync(package, CancellationToken.None);
        await File.WriteAllBytesAsync(package.Path, Encoding.UTF8.GetBytes("tampered"), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => GitHubReleaseUpdateService.VerifyFileAsync(package, CancellationToken.None));
    }

    [Theory]
    [InlineData("https://evil.example/update.exe")]
    [InlineData("http://release-assets.githubusercontent.com/file")]
    [InlineData("https://user@github.com/file")]
    [InlineData("https://github.com:444/file")]
    public async Task Untrusted_redirects_leave_no_partial_package(string destination)
    {
        using var scope = new ServiceScope(request =>
        {
            if (request.RequestUri!.Host == "api.github.com") return JsonResponse();
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(destination);
            return response;
        });
        var release = (await scope.Service.CheckAsync(CancellationToken.None))!;
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Service.DownloadAsync(release, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scope.Directory));
    }

    [Fact]
    public async Task Wrong_hash_removes_only_this_attempt_and_preserves_previous_files()
    {
        using var scope = new ServiceScope(request => request.RequestUri!.Host == "api.github.com"
            ? JsonResponse() : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[Payload.Length]) });
        Directory.CreateDirectory(scope.Directory);
        var preserved = Path.Combine(scope.Directory, "previous.zip");
        await File.WriteAllTextAsync(preserved, "keep", TestContext.Current.CancellationToken);
        var release = (await scope.Service.CheckAsync(CancellationToken.None))!;
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Service.DownloadAsync(release, CancellationToken.None));
        Assert.Equal(new[] { preserved }, Directory.EnumerateFileSystemEntries(scope.Directory));
    }

    [Fact]
    public async Task Cancellation_during_stream_copy_removes_the_partial_file()
    {
        using var blocked = new BlockingStream();
        using var scope = new ServiceScope(request => request.RequestUri!.Host == "api.github.com"
            ? JsonResponse() : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(blocked) });
        var release = (await scope.Service.CheckAsync(CancellationToken.None))!;
        using var cancellation = new CancellationTokenSource();
        var download = scope.Service.DownloadAsync(release, cancellation.Token);
        await blocked.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Empty(Directory.EnumerateFileSystemEntries(scope.Directory));
    }

    [Fact]
    public void Installation_requires_exact_executable_path_and_ownership_manifest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Colibri-update-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.False(WindowsUpdateHandoff.IsOwnedInstallation(Path.Combine(directory, "Colibri.exe"), directory));
            File.WriteAllText(Path.Combine(directory, "installed-files.json"), "{\"format\":1,\"product\":\"Colibri\",\"files\":[\"Colibri.exe\"]}");
            Assert.True(WindowsUpdateHandoff.IsOwnedInstallation(Path.Combine(directory, "Colibri.exe"), directory));
            Assert.False(WindowsUpdateHandoff.IsOwnedInstallation(Path.Combine(directory, "portable", "Colibri.exe"), directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class BlockingStream : Stream
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
    private sealed class ServiceScope : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "Colibri-update-tests", Guid.NewGuid().ToString("N"));
        public GitHubReleaseUpdateService Service { get; }
        public ServiceScope(Func<HttpRequestMessage, HttpResponseMessage> respond, TimeProvider? time = null)
        {
            Service = new GitHubReleaseUpdateService(new HttpClient(new Handler(respond)), "2.0.0", UpdatePackageKind.Portable, Directory, time);
        }
        public void Dispose() { Service.Dispose(); if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
    }
}
