using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using UpskillTracker.Services;
using Xunit;

namespace UpskillTracker.Tests;

public sealed class AnnouncementFeedTests
{
    [Fact]
    public async Task FailedRefreshDoesNotEraseTheLastGoodFeed()
    {
        using var handler = new FeedHandler();
        using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AnnouncementFeedService(client, cache, NullLogger<AnnouncementFeedService>.Instance);
        var original = await service.GetAnnouncementsAsync();
        Assert.NotEmpty(original);

        handler.Fail = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => service.RefreshAsync());
        var cached = await service.GetAnnouncementsAsync();
        Assert.Same(original, cached);
    }

    [Fact]
    public async Task TotalSourceFailureIsReportedInsteadOfClaimingAnEmptySuccessfulRefresh()
    {
        using var handler = new FeedHandler { Fail = true };
        using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AnnouncementFeedService(client, cache, NullLogger<AnnouncementFeedService>.Instance);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetAnnouncementsAsync());
        handler.Fail = false;
        Assert.NotEmpty(await service.GetAnnouncementsAsync());
    }

    [Fact]
    public async Task OneUnavailableSourceDoesNotTakeDownTheFeed()
    {
        using var handler = new FeedHandler { FailOneSource = true };
        using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AnnouncementFeedService(client, cache, NullLogger<AnnouncementFeedService>.Instance);
        Assert.NotEmpty(await service.GetAnnouncementsAsync());
    }

    private sealed class FeedHandler : HttpMessageHandler
    {
        public bool Fail { get; set; }
        public bool FailOneSource { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Fail || (FailOneSource && request.RequestUri?.Host == "github.blog"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    <rss><channel><item>
                    <title>GitHub Copilot developer update</title>
                    <link>https://github.blog/test-learning-update</link>
                    <description>GitHub Copilot tools for developers.</description>
                    <pubDate>Mon, 07 Sep 2026 08:00:00 GMT</pubDate>
                    </item></channel></rss>
                    """)
            });
        }
    }
}
