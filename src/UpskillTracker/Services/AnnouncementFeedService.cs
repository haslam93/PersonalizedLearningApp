using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Caching.Memory;
using UpskillTracker.Models;

namespace UpskillTracker.Services;

public partial class AnnouncementFeedService(HttpClient httpClient, IMemoryCache cache, ILogger<AnnouncementFeedService> logger)
{
    private const string CacheKey = "announcements:latest";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    private static readonly FeedSource[] FeedSources =
    [
        new("Azure Updates", "https://www.microsoft.com/releasecommunications/api/v2/azure/rss", AnnouncementStream.MicrosoftOfficial, "Azure platform"),
        new("Microsoft Foundry Blog", "https://devblogs.microsoft.com/foundry/feed/", AnnouncementStream.MicrosoftOfficial, "Azure AI Foundry"),
        new("GitHub Copilot Blog", "https://github.blog/tag/github-copilot/feed/", AnnouncementStream.MicrosoftOfficial, "GitHub Copilot"),
        new("Apps on Azure Blog", "https://techcommunity.microsoft.com/t5/s/gxcuf89792/rss/board?board.id=AppsonAzureBlog", AnnouncementStream.MicrosoftOfficial, "App Service"),
        new("Azure Integration Services Blog", "https://techcommunity.microsoft.com/t5/s/gxcuf89792/rss/board?board.id=IntegrationsonAzureBlog", AnnouncementStream.MicrosoftOfficial, "Azure API Management"),
        new("Simon Willison", "https://simonwillison.net/atom/everything/", AnnouncementStream.IndustryInsights, "Developer Tools"),
        new("OpenAI News", "https://openai.com/news/rss.xml", AnnouncementStream.IndustryInsights, "Industry"),
        new("Scott Hanselman", "https://www.hanselman.com/blog/rss", AnnouncementStream.IndustryInsights, "Developer Tools"),
        new("Microsoft Research", "https://www.microsoft.com/en-us/research/feed/", AnnouncementStream.IndustryInsights, "Research"),
        new("Google Research", "https://research.google/blog/rss/", AnnouncementStream.IndustryInsights, "Research"),
        new("AWS Machine Learning Blog", "https://aws.amazon.com/blogs/machine-learning/feed/", AnnouncementStream.IndustryInsights, "Industry"),
        new("One Useful Thing", "https://www.oneusefulthing.org/feed", AnnouncementStream.IndustryInsights, "Industry"),
        new("Andrej Karpathy", "https://karpathy.bearblog.dev/feed/", AnnouncementStream.IndustryInsights, "LLMs"),
        new("Anthropic Research", "https://www.anthropic.com/research", AnnouncementStream.IndustryInsights, "Research", FeedSourceKind.HtmlPage)
    ];

    public async Task<IReadOnlyList<AnnouncementItem>> GetAnnouncementsAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue<IReadOnlyList<AnnouncementItem>>(CacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var announcements = await RefreshAnnouncementsAsync(cancellationToken);
        cache.Set(CacheKey, announcements, CacheDuration);
        return announcements;
    }

    public Task<IReadOnlyList<AnnouncementItem>> RefreshAsync(CancellationToken cancellationToken = default)
        => RefreshAnnouncementsAsync(cancellationToken);

    private async Task<IReadOnlyList<AnnouncementItem>> RefreshAnnouncementsAsync(CancellationToken cancellationToken)
    {
        var fetchTasks = FeedSources.Select(source => FetchFeedAsync(source, cancellationToken));
        var feedResults = await Task.WhenAll(fetchTasks);

        var announcements = feedResults
            .SelectMany(result => result)
            .GroupBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.PublishedUtc).First())
            .OrderByDescending(item => item.PublishedUtc)
            .Take(18)
            .ToList();

        cache.Set(CacheKey, announcements, CacheDuration);
        return announcements;
    }

    private async Task<IReadOnlyList<AnnouncementItem>> FetchFeedAsync(FeedSource source, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(source.FeedUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);

            return source.Kind switch
            {
                FeedSourceKind.HtmlPage => ParseHtmlPage(payload, source),
                _ => ParseFeed(XDocument.Parse(payload, LoadOptions.PreserveWhitespace), source)
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to refresh announcement feed from {FeedUrl}", source.FeedUrl);
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
            .Select(item => CreateAnnouncement(
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

                return CreateAnnouncement(
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

    private static AnnouncementItem? CreateAnnouncement(FeedSource source, string? title, string? url, string? summary, string? publishedValue)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var normalizedSummary = NormalizeSummary(summary);
        var combinedText = string.Join(' ', title, normalizedSummary);
        var topic = ResolveTopic(source, combinedText);

        if (string.IsNullOrWhiteSpace(topic))
        {
            return null;
        }

        _ = DateTimeOffset.TryParse(publishedValue, out var published);

        return new AnnouncementItem
        {
            Stream = source.Stream,
            Title = WebUtility.HtmlDecode(title.Trim()),
            Url = url.Trim(),
            Summary = normalizedSummary,
            PublishedUtc = (published == default ? DateTimeOffset.UtcNow : published).UtcDateTime,
            Source = source.Name,
            Topic = topic,
            SourceUrl = source.FeedUrl
        };
    }

    private static string ResolveTopic(FeedSource source, string combinedText)
    {
        if (!string.Equals(source.DefaultTopic, "Azure platform", StringComparison.OrdinalIgnoreCase))
        {
            return source.DefaultTopic;
        }

        if (ContainsAny(combinedText, "foundry", "azure ai foundry", "azure ai studio"))
        {
            return "Azure AI Foundry";
        }

        if (ContainsAny(combinedText, "copilot", "github copilot"))
        {
            return "GitHub Copilot";
        }

        if (ContainsAny(combinedText, "api management", "apim", "ai gateway"))
        {
            return "Azure API Management";
        }

        if (ContainsAny(combinedText, "app service", "web apps", "web app for containers"))
        {
            return "App Service";
        }

        if (ContainsAny(combinedText, "azure ai", "azure ai search", "container apps", "service bus", "logic apps", "agent framework", "sre agent", "application insights"))
        {
            return "Azure platform";
        }

        return string.Empty;
    }

    private static IReadOnlyList<AnnouncementItem> ParseHtmlPage(string html, FeedSource source)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return [];
        }

        if (string.Equals(source.Name, "Anthropic Research", StringComparison.OrdinalIgnoreCase))
        {
            return ParseAnthropicResearchPage(html, source);
        }

        return [];
    }

    private static IReadOnlyList<AnnouncementItem> ParseAnthropicResearchPage(string html, FeedSource source)
    {
        var matches = AnthropicPublicationRegex().Matches(html);
        var announcements = new List<AnnouncementItem>();

        foreach (Match match in matches)
        {
            var date = match.Groups["date"].Value;
            var href = match.Groups["href"].Value;
            var title = WebUtility.HtmlDecode(match.Groups["title"].Value).Trim();

            if (string.IsNullOrWhiteSpace(date) || string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var absoluteUrl = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? href
                : $"https://www.anthropic.com{href}";

            var item = CreateAnnouncement(source, title, absoluteUrl, string.Empty, date);
            if (item is not null)
            {
                announcements.Add(item);
            }
        }

        return announcements;
    }

    private static bool ContainsAny(string value, params string[] candidates)
        => candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

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

    private sealed record FeedSource(
        string Name,
        string FeedUrl,
        AnnouncementStream Stream,
        string DefaultTopic,
        FeedSourceKind Kind = FeedSourceKind.XmlFeed);

    private enum FeedSourceKind
    {
        XmlFeed,
        HtmlPage
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("(?is)(?<date>(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\\s+\\d{1,2},\\s+\\d{4}).{0,300}?href=\"(?<href>/research/[^\"]+)\"[^>]*>(?<title>.*?)</a>")]
    private static partial Regex AnthropicPublicationRegex();
}