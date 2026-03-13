using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Caching.Memory;
using UpskillTracker.Models;

namespace UpskillTracker.Services;

public partial class ThoughtLeaderFeedService(HttpClient httpClient, IMemoryCache cache, ILogger<ThoughtLeaderFeedService> logger)
{
    private const string CacheKey = "thoughtleaders:latest";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    private static readonly FeedSource[] FeedSources =
    [
        new("Ethan Mollick", "https://www.oneusefulthing.org/feed", "AI & Education"),
        new("Andrej Karpathy", "https://karpathy.github.io/feed.xml", "AI Research"),
        new("Simon Willison", "https://simonwillison.net/atom/everything/", "AI & Web"),
        new("Lilian Weng", "https://lilianweng.github.io/index.xml", "AI Research"),
        new("Benedict Evans", "https://www.ben-evans.com/benedictevans?format=rss", "Technology Strategy"),
    ];

    public async Task<IReadOnlyList<AnnouncementItem>> GetPostsAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue<IReadOnlyList<AnnouncementItem>>(CacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        return await RefreshPostsAsync(cancellationToken);
    }

    public Task<IReadOnlyList<AnnouncementItem>> RefreshAsync(CancellationToken cancellationToken = default)
        => RefreshPostsAsync(cancellationToken);

    private async Task<IReadOnlyList<AnnouncementItem>> RefreshPostsAsync(CancellationToken cancellationToken)
    {
        var fetchTasks = FeedSources.Select(source => FetchFeedAsync(source, cancellationToken));
        var feedResults = await Task.WhenAll(fetchTasks);

        var posts = feedResults
            .SelectMany(result => result)
            .GroupBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.PublishedUtc).First())
            .OrderByDescending(item => item.PublishedUtc)
            .Take(18)
            .ToList();

        cache.Set(CacheKey, posts, CacheDuration);
        return posts;
    }

    private async Task<IReadOnlyList<AnnouncementItem>> FetchFeedAsync(FeedSource source, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(source.FeedUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            var xml = await response.Content.ReadAsStringAsync(cancellationToken);
            var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            return ParseFeed(document, source);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to refresh thought leader feed from {FeedUrl}", source.FeedUrl);
            return [];
        }
    }

    private static IReadOnlyList<AnnouncementItem> ParseFeed(XDocument document, FeedSource source)
    {
        var rootName = document.Root?.Name.LocalName;

        return rootName switch
        {
            "rss" => ParseRss(document, source),
            "feed" => ParseAtom(document, source),
            _ => []
        };
    }

    private static IReadOnlyList<AnnouncementItem> ParseRss(XDocument document, FeedSource source)
    {
        var items = document.Root?
            .Element("channel")?
            .Elements("item")
            ?? [];

        return items
            .Select(item => CreatePost(
                source,
                item.Element("title")?.Value,
                item.Element("link")?.Value,
                item.Element("description")?.Value,
                item.Element("pubDate")?.Value))
            .Where(item => item is not null)
            .Cast<AnnouncementItem>()
            .ToList();
    }

    private static IReadOnlyList<AnnouncementItem> ParseAtom(XDocument document, FeedSource source)
    {
        XNamespace atomNs = document.Root?.GetDefaultNamespace() ?? "http://www.w3.org/2005/Atom";
        var entries = document.Root?.Elements(atomNs + "entry") ?? [];

        return entries
            .Select(entry =>
            {
                var link = entry.Elements(atomNs + "link")
                    .Select(candidate => candidate.Attribute("href")?.Value)
                    .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));

                return CreatePost(
                    source,
                    entry.Element(atomNs + "title")?.Value,
                    link,
                    entry.Element(atomNs + "summary")?.Value ?? entry.Element(atomNs + "content")?.Value,
                    entry.Element(atomNs + "published")?.Value ?? entry.Element(atomNs + "updated")?.Value);
            })
            .Where(item => item is not null)
            .Cast<AnnouncementItem>()
            .ToList();
    }

    private static AnnouncementItem? CreatePost(FeedSource source, string? title, string? url, string? summary, string? publishedValue)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var normalizedSummary = NormalizeSummary(summary);
        _ = DateTimeOffset.TryParse(publishedValue, out var published);

        return new AnnouncementItem
        {
            Title = WebUtility.HtmlDecode(title.Trim()),
            Url = url.Trim(),
            Summary = normalizedSummary,
            PublishedUtc = (published == default ? DateTimeOffset.UtcNow : published).UtcDateTime,
            Source = source.Name,
            Topic = source.DefaultTopic,
            SourceUrl = source.FeedUrl
        };
    }

    private static string NormalizeSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return string.Empty;
        }

        var decoded = WebUtility.HtmlDecode(summary);
        var withoutMarkup = HtmlRegex().Replace(decoded, " ");
        var normalizedWhitespace = WhitespaceRegex().Replace(withoutMarkup, " ").Trim();
        return normalizedWhitespace.Length <= 220
            ? normalizedWhitespace
            : string.Concat(normalizedWhitespace[..217], "...");
    }

    private sealed record FeedSource(string Name, string FeedUrl, string DefaultTopic);

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}
