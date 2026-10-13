using System.Net;
using System.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using UpskillTracker.Models;
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

    [Fact]
    public async Task FabricUpdatesAreRetainedEvenWhenOtherOfficialSourcesAreNewer()
    {
        using var handler = new FixtureHandler(uri =>
        {
            var fabric = uri.Host == "community.fabric.microsoft.com";
            return Feed(Enumerable.Range(0, 8).Select(index => Entry(
                fabric ? $"Microsoft Fabric update {index}" : $"Microsoft Foundry update {index}",
                $"https://example.com/{Uri.EscapeDataString(uri.AbsoluteUri)}/{index}",
                fabric ? "Thu, 01 Oct 2026 08:00:00 GMT" : "Tue, 13 Oct 2026 08:00:00 GMT")));
        });
        using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AnnouncementFeedService(client, cache, NullLogger<AnnouncementFeedService>.Instance);
        var announcements = await service.GetAnnouncementsAsync();
        Assert.Equal(5, announcements.Count(item => item.Source == "Microsoft Fabric Blog"));
        Assert.All(announcements.GroupBy(item => item.Source), group => Assert.True(group.Count() <= 5));
    }

    [Fact]
    public async Task CopilotStudioAndContainerAppsAreNotAssignedTheWrongProductByTheirSource()
    {
        using var handler = new FixtureHandler(uri => uri.AbsoluteUri.Contains("/power-platform/blog/feed/")
            ? Feed([Entry("Copilot Studio knowledge tools", "https://example.com/studio"),
                Entry("Power Pages regional expansion", "https://example.com/pages")])
            : uri.AbsoluteUri.Contains("AppsonAzureBlog")
                ? Feed([Entry("Azure Container Apps sandboxes", "https://example.com/containers")]) : Feed([]));
        using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AnnouncementFeedService(client, cache, NullLogger<AnnouncementFeedService>.Instance);
        var announcements = await service.GetAnnouncementsAsync();
        Assert.Equal("Copilot Studio", announcements.Single(item => item.Url.EndsWith("/studio")).Topic);
        Assert.Equal("Container Apps", announcements.Single(item => item.Url.EndsWith("/containers")).Topic);
        Assert.DoesNotContain(announcements, item => item.Url.EndsWith("/pages"));
        Assert.DoesNotContain(announcements, item => item.Topic == "GitHub");
    }

    [Fact]
    public async Task AnIntegrationBlogPostIsNotAutomaticallyAnApimAnnouncement()
    {
        using var handler = new FixtureHandler(uri => uri.AbsoluteUri.Contains("IntegrationsonAzureBlog")
            ? Feed([Entry("Logic Apps Standard managed identity", "https://example.com/logic-apps")]) : Feed([]));
        using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AnnouncementFeedService(client, cache, NullLogger<AnnouncementFeedService>.Instance);
        var item = Assert.Single(await service.GetAnnouncementsAsync());
        Assert.Equal("Integration Services", item.Topic);
        Assert.False(LearningProductCatalog.Matches(LearningProductCatalog.Products.Single(product => product.Key == "apim"), item));
    }

    [Fact]
    public async Task UnsafeLinksAndUnknownPublicationDatesDoNotBecomeFreshUpdates()
    {
        using var handler = new FixtureHandler(_ => Feed([
            Entry("Microsoft Foundry valid update", "https://example.com/valid"),
            Entry("Microsoft Foundry unsafe update", "javascript:alert(1)"),
            Entry("Microsoft Foundry undated update", "https://example.com/undated", "")
        ]));
        using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AnnouncementFeedService(client, cache, NullLogger<AnnouncementFeedService>.Instance);
        var item = Assert.Single(await service.GetAnnouncementsAsync());
        Assert.Equal("https://example.com/valid", item.Url);
        Assert.Equal(new DateTime(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc), item.PublishedUtc);
    }

    [Fact]
    public async Task SuccessfulHttpResponsesWithNoUsableFeedAreNotCachedAsSuccess()
    {
        using var handler = new FixtureHandler(_ => "<html><body>Sign in to continue</body></html>");
        using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new AnnouncementFeedService(client, cache, NullLogger<AnnouncementFeedService>.Instance);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetAnnouncementsAsync());
    }

    private static string Feed(IEnumerable<string> entries) => $"<rss><channel>{string.Join("", entries)}</channel></rss>";
    private static string Entry(string title, string url, string date = "Tue, 13 Oct 2026 08:00:00 GMT")
        => $"<item><title>{SecurityElement.Escape(title)}</title><link>{SecurityElement.Escape(url)}</link><pubDate>{date}</pubDate></item>";

    private sealed class FixtureHandler(Func<Uri, string> payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload(request.RequestUri!))
            });
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
