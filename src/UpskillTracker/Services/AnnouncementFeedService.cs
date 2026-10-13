using System.Net;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Caching.Memory;
using UpskillTracker.Models;

namespace UpskillTracker.Services;

public partial class AnnouncementFeedService(HttpClient httpClient, IMemoryCache cache, ILogger<AnnouncementFeedService> logger)
{
    private const string CacheKey = "announcements:latest";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);
    private const int MaxItemsPerSource = 5;
    private const int MaxItemsPerStream = 24;

    private static readonly FeedSource[] FeedSources =
    [
        new("Azure Updates", "https://www.microsoft.com/releasecommunications/api/v2/azure/rss", AnnouncementStream.MicrosoftOfficial, "Azure platform"),
        new("Microsoft Foundry Blog", "https://devblogs.microsoft.com/foundry/feed/", AnnouncementStream.MicrosoftOfficial, "Azure AI Foundry"),
        new("GitHub Copilot Blog", "https://github.blog/tag/github-copilot/feed/", AnnouncementStream.MicrosoftOfficial, "GitHub Copilot"),
        new("Apps on Azure Blog", "https://techcommunity.microsoft.com/t5/s/gxcuf89792/rss/board?board.id=AppsonAzureBlog", AnnouncementStream.MicrosoftOfficial, "App Innovation"),
        new("Azure Integration Services Blog", "https://techcommunity.microsoft.com/t5/s/gxcuf89792/rss/board?board.id=IntegrationsonAzureBlog", AnnouncementStream.MicrosoftOfficial, "Integration Services"),
        new("Microsoft Fabric Blog", "https://community.fabric.microsoft.com/t5/s/rss/board?board.id=fbc_fabricupdatesblogs", AnnouncementStream.MicrosoftOfficial, "Microsoft Fabric"),
        new("Microsoft Power Platform Blog", "https://www.microsoft.com/en-us/power-platform/blog/feed/", AnnouncementStream.MicrosoftOfficial, "Power Platform"),
        new("Yina Arenas", "https://azure.microsoft.com/en-us/blog/author/yina-arenas/feed/", AnnouncementStream.MicrosoftOfficial, "Azure AI Foundry"),
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

        return await RefreshAnnouncementsAsync(cancellationToken);
    }

    public Task<IReadOnlyList<AnnouncementItem>> RefreshAsync(CancellationToken cancellationToken = default)
        => RefreshAnnouncementsAsync(cancellationToken);

    private async Task<IReadOnlyList<AnnouncementItem>> RefreshAnnouncementsAsync(CancellationToken cancellationToken)
    {
        var fetchTasks = FeedSources.Select(source => FetchFeedAsync(source, cancellationToken));
        var feedResults = await Task.WhenAll(fetchTasks);
        if (feedResults.All(result => !result.Succeeded))
        {
            throw new HttpRequestException("No usable announcements could be loaded. Previously loaded updates have been kept.");
        }

        var announcements = feedResults
            .SelectMany(result => result.Items)
            .GroupBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.PublishedUtc).First())
            .GroupBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.OrderByDescending(item => item.PublishedUtc).Take(MaxItemsPerSource))
            .GroupBy(item => item.Stream)
            .SelectMany(group => group.OrderByDescending(item => item.PublishedUtc)
                .Take(group.Key == AnnouncementStream.MicrosoftOfficial ? FeedSources.Length * MaxItemsPerSource : MaxItemsPerStream))
            .OrderByDescending(item => item.PublishedUtc)
            .ToList();

        cache.Set(CacheKey, announcements, CacheDuration);
        return announcements;
    }

    private sealed record FeedFetchResult(IReadOnlyList<AnnouncementItem> Items, bool Succeeded);

    private async Task<FeedFetchResult> FetchFeedAsync(FeedSource source, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(source.FeedUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);

            var items = source.Kind switch
            {
                FeedSourceKind.HtmlPage => ParseHtmlPage(payload, source),
                _ => ParseFeed(XDocument.Parse(payload, LoadOptions.PreserveWhitespace), source)
            };
            if (items.Count == 0)
            {
                logger.LogWarning("No usable announcements were returned by {FeedUrl}", source.FeedUrl);
            }
            return new FeedFetchResult(items, items.Count > 0);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException ||
            ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Unable to refresh announcement feed from {FeedUrl}", source.FeedUrl);
            return new FeedFetchResult([], false);
        }
    }

    private static IReadOnlyList<AnnouncementItem> ParseFeed(XDocument document, FeedSource source)
    {
        var rootName = document.Root?.Name.LocalName;

        return rootName switch
        {
            "rss" => ParseRss(document, source),
            "feed" => ParseAtom(document, source),
            _ => throw new System.Xml.XmlException("Expected an RSS or Atom announcement feed.")
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
        if (string.IsNullOrWhiteSpace(title) || !Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") ||
            !DateTimeOffset.TryParse(publishedValue, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var published))
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

        return new AnnouncementItem
        {
            Stream = source.Stream,
            Title = WebUtility.HtmlDecode(title.Trim()),
            Url = uri.AbsoluteUri,
            Summary = normalizedSummary,
            PublishedUtc = published.UtcDateTime,
            Source = source.Name,
            Topic = topic,
            SourceUrl = source.FeedUrl
        };
    }

    private static string ResolveTopic(FeedSource source, string combinedText)
    {
        if (source.DefaultTopic == "Power Platform")
        {
            return LearningProductCatalog.Products.First(product => product.Key == "copilot-studio").Matches(combinedText)
                ? "Copilot Studio" : string.Empty;
        }

        var product = LearningProductCatalog.ResolveTopic(combinedText);
        if (product is not null && source.Stream == AnnouncementStream.MicrosoftOfficial)
        {
            return product;
        }

        if (!string.Equals(source.DefaultTopic, "Azure platform", StringComparison.OrdinalIgnoreCase))
        {
            return source.DefaultTopic;
        }

        if (ContainsAny(combinedText, "foundry", "azure ai foundry", "azure ai studio"))
        {
            return "Azure AI Foundry";
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
            var publishedValue = match.Groups["published"].Value.Trim();
            var href = match.Groups["href"].Value.Trim();
            var title = CleanInlineText(match.Groups["title"].Value);

            if (string.IsNullOrWhiteSpace(publishedValue) || string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var absoluteUrl = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? href
                : $"https://www.anthropic.com{href}";

            var item = CreateAnnouncement(source, title, absoluteUrl, string.Empty, publishedValue);
            if (item is not null)
            {
                announcements.Add(item);
            }
        }

        return announcements;
    }

    private static string CleanInlineText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var withoutMarkup = HtmlRegex().Replace(value, " ");
        var decoded = WebUtility.HtmlDecode(withoutMarkup);
        return WhitespaceRegex().Replace(decoded, " ").Trim();
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

    [GeneratedRegex(@"(?is)<a\s+href=""(?<href>/research/[^""]+)""\s+class=""PublicationList-[^""]*__listItem""[^>]*>\s*<div\s+class=""PublicationList-[^""]*__meta""[^>]*>\s*<time[^>]*>(?<published>(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\s+\d{1,2},\s+\d{4})</time>.*?</div>\s*<span\s+class=""PublicationList-[^""]*__title[^""]*"">(?<title>.*?)</span>")]
    private static partial Regex AnthropicPublicationRegex();
}