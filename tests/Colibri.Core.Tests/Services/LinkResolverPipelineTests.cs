using Colibri.Core.Abstractions;
using Colibri.Core.Models;
using Colibri.Core.Services;
using Microsoft.Extensions.Logging;

namespace Colibri.Core.Tests.Services;

public class LinkResolverPipelineTests
{
    private static readonly Uri Url = new("https://example.com/file.zip");

    [Fact]
    public async Task Asks_resolvers_from_highest_to_lowest_priority()
    {
        var calls = new List<string>();
        var resolvers = new[]
        {
            new FakeResolver("low", -5, calls),
            new FakeResolver("high", 100, calls),
            new FakeResolver("mid", 0, calls),
        };

        await new LinkResolverPipeline(resolvers).ResolveAsync(Url, LinkContext.Empty, TestContext.Current.CancellationToken);

        Assert.Equal(["high", "mid", "low"], calls);
    }

    [Fact]
    public async Task First_non_null_answer_wins_and_later_resolvers_are_not_asked()
    {
        var calls = new List<string>();
        var expected = new[] { Request("https://example.com/a") };
        var resolvers = new[]
        {
            new FakeResolver("high", 10, calls),
            new FakeResolver("mid", 5, calls, expected),
            new FakeResolver("low", 0, calls, [Request("https://example.com/b")]),
        };

        var result = await new LinkResolverPipeline(resolvers).ResolveAsync(Url, LinkContext.Empty, TestContext.Current.CancellationToken);

        Assert.Equal(expected.Select(r => r.Uri), result.Select(r => r.Uri));
        Assert.Equal(["high", "mid"], calls);
    }

    [Fact]
    public async Task Equal_priorities_keep_registration_order()
    {
        var calls = new List<string>();
        var resolvers = new[] { new FakeResolver("first", 1, calls), new FakeResolver("second", 1, calls) };

        await new LinkResolverPipeline(resolvers).ResolveAsync(Url, LinkContext.Empty, TestContext.Current.CancellationToken);

        Assert.Equal(["first", "second"], calls);
    }

    [Fact]
    public async Task Throwing_resolver_is_skipped()
    {
        var calls = new List<string>();
        var expected = new[] { Request("https://example.com/ok") };
        var resolvers = new[]
        {
            new FakeResolver("broken", 10, calls, error: new HttpRequestException("boom")),
            new FakeResolver("working", 0, calls, expected),
        };

        var result = await new LinkResolverPipeline(resolvers).ResolveAsync(Url, LinkContext.Empty, TestContext.Current.CancellationToken);

        Assert.Equal(expected.Select(r => r.Uri), result.Select(r => r.Uri));
        Assert.Equal(["broken", "working"], calls);
    }

    [Fact]
    public async Task Resolver_cancelling_on_its_own_is_treated_as_a_failure()
    {
        var expected = new[] { Request("https://example.com/ok") };
        var resolvers = new[]
        {
            new FakeResolver("timeout", 10, [], error: new TaskCanceledException()),
            new FakeResolver("working", 0, [], expected),
        };

        var result = await new LinkResolverPipeline(resolvers).ResolveAsync(Url, LinkContext.Empty, TestContext.Current.CancellationToken);

        Assert.Equal(expected.Select(r => r.Uri), result.Select(r => r.Uri));
    }

    [Fact]
    public async Task No_resolvers_returns_empty()
    {
        var result = await new LinkResolverPipeline([]).ResolveAsync(Url, LinkContext.Empty, TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task All_resolvers_declining_returns_empty()
    {
        var resolvers = new[] { new FakeResolver("a", 1, []), new FakeResolver("b", 0, []) };

        var result = await new LinkResolverPipeline(resolvers).ResolveAsync(Url, LinkContext.Empty, TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Cancelled_token_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var resolvers = new[] { new FakeResolver("a", 1, [], [Request("https://example.com/a")]) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new LinkResolverPipeline(resolvers).ResolveAsync(Url, LinkContext.Empty, cts.Token));
    }

    [Fact]
    public async Task Direct_resolver_is_the_fallback()
    {
        var resolvers = new ILinkResolver[] { new DirectLinkResolver(), new FakeResolver("declines", 0, []) };

        var result = await new LinkResolverPipeline(resolvers).ResolveAsync(Url, LinkContext.Empty, TestContext.Current.CancellationToken);

        var request = Assert.Single(result);
        Assert.Equal(Url, request.Uri);
    }

    [Fact]
    public async Task Empty_answer_means_handled_and_stops_the_chain()
    {
        var calls = new List<string>();
        var resolvers = new[] { new FakeResolver("handles", 1, calls, []), new FakeResolver("fallback", 0, calls, [Request("https://example.com/b")]) };

        var result = await new LinkResolverPipeline(resolvers).ResolveAsync(Url, LinkContext.Empty, TestContext.Current.CancellationToken);

        Assert.Empty(result);
        Assert.Equal(["handles"], calls);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/plain,hi")]
    public async Task Drops_resolver_output_that_fails_the_url_policy(string badUrl)
    {
        var good = Request("https://example.com/good.zip");
        var logger = new ListLogger();
        var resolvers = new[] { new FakeResolver("mixed", 0, [], [Request(badUrl), good]) };

        var result = await new LinkResolverPipeline(resolvers, logger).ResolveAsync(Url, LinkContext.Empty, TestContext.Current.CancellationToken);

        Assert.Equal(good.Uri, Assert.Single(result).Uri);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Drops_invalid_headers_from_resolver_output()
    {
        var request = new DownloadRequest
        {
            Uri = new Uri("https://example.com/a.zip"),
            Referrer = "bad\r\nvalue",
            UserAgent = "ok-agent",
            Cookies = "c=1\0",
            Headers = new Dictionary<string, string> { ["X-Good"] = "1", ["Bad Name"] = "2", ["X-Bad"] = "a\nb" },
        };
        var resolvers = new[] { new FakeResolver("r", 0, [], [request]) };

        var result = await new LinkResolverPipeline(resolvers).ResolveAsync(Url, LinkContext.Empty, TestContext.Current.CancellationToken);

        var cleaned = Assert.Single(result);
        Assert.Null(cleaned.Referrer);
        Assert.Null(cleaned.Cookies);
        Assert.Equal("ok-agent", cleaned.UserAgent);
        Assert.Equal("X-Good", Assert.Single(cleaned.Headers).Key);
    }

    [Fact]
    public async Task Logs_urls_without_credentials_or_query()
    {
        var logger = new ListLogger();
        var url = new Uri("https://user:secret@example.com/a.zip?token=abc123");
        var resolvers = new[]
        {
            new FakeResolver("broken", 1, [], error: new InvalidOperationException("boom")),
            new FakeResolver("bad-output", 0, [], [Request("ftp://user:pw2@ftp.example.com/x?sig=zzz"), Request("file:///etc/passwd")]),
        };

        await new LinkResolverPipeline(resolvers, logger).ResolveAsync(url, LinkContext.Empty, TestContext.Current.CancellationToken);

        Assert.NotEmpty(logger.Entries);
        foreach (var entry in logger.Entries)
        {
            Assert.DoesNotContain("secret", entry.Message);
            Assert.DoesNotContain("abc123", entry.Message);
        }
    }

    private static DownloadRequest Request(string url) => new() { Uri = new Uri(url) };

    private sealed class ListLogger : ILogger<LinkResolverPipeline>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class FakeResolver(
        string id,
        int priority,
        List<string> calls,
        IReadOnlyList<DownloadRequest>? result = null,
        Exception? error = null) : ILinkResolver
    {
        public string Id => id;

        public int Priority => priority;

        public Task<IReadOnlyList<DownloadRequest>?> ResolveAsync(Uri url, LinkContext context, CancellationToken ct)
        {
            calls.Add(id);
            return error is null ? Task.FromResult(result) : Task.FromException<IReadOnlyList<DownloadRequest>?>(error);
        }
    }
}
