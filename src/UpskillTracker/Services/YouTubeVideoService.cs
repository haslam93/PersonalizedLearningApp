using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UpskillTracker.Data;
using UpskillTracker.Models;

namespace UpskillTracker.Services;

public partial class YouTubeVideoService(
    HttpClient httpClient,
    IDbContextFactory<TrackerDbContext> dbFactory,
    IOptions<YouTubeOptions> options,
    ILogger<YouTubeVideoService> logger)
{
    private readonly YouTubeOptions settings = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.ApiKey);

    public string GetSetupMessage()
        => IsConfigured
            ? string.Empty
            : "Add a YouTube API key in configuration to refresh channels and videos. Existing saved state will remain available.";

    public async Task<YouTubeChannelResolutionResult> ResolveChannelAsync(string input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return YouTubeChannelResolutionResult.Failure("Enter a YouTube channel URL, handle, or channel ID.");
        }

        if (!IsConfigured)
        {
            return YouTubeChannelResolutionResult.Failure(GetSetupMessage());
        }

        var normalizedInput = input.Trim();
        var channelId = TryExtractChannelId(normalizedInput);

        if (string.IsNullOrWhiteSpace(channelId))
        {
            channelId = await ResolveChannelIdFromInputAsync(normalizedInput, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(channelId))
        {
            return YouTubeChannelResolutionResult.Failure("That channel could not be resolved. Use a YouTube handle URL such as https://www.youtube.com/@MicrosoftDeveloper or a direct channel URL.");
        }

        var channel = await GetChannelByIdAsync(channelId, cancellationToken);
        return channel is null
            ? YouTubeChannelResolutionResult.Failure("That channel could not be loaded from the YouTube Data API.")
            : YouTubeChannelResolutionResult.Success(channel);
    }

    public async Task<YouTubeSyncResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return new YouTubeSyncResult(false, 0, 0, GetSetupMessage());
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var channels = await db.VideoChannels
            .OrderBy(channel => channel.DisplayName)
            .ToListAsync(cancellationToken);

        if (channels.Count == 0)
        {
            return new YouTubeSyncResult(true, 0, 0, "No YouTube channels are saved yet.");
        }

        var refreshedChannels = 0;
        var upsertedVideos = 0;
        var now = DateTime.UtcNow;

        foreach (var channel in channels)
        {
            try
            {
                var resolved = await ResolveStoredChannelAsync(channel, cancellationToken);
                if (resolved is null)
                {
                    continue;
                }

                channel.DisplayName = resolved.DisplayName;
                channel.Handle = resolved.Handle;
                channel.ChannelId = resolved.ChannelId;
                channel.ChannelUrl = resolved.ChannelUrl;
                channel.Description = resolved.Description;
                channel.ThumbnailUrl = resolved.ThumbnailUrl;
                channel.UpdatedUtc = now;
                channel.LastSyncedUtc = now;

                var existingVideos = await db.Videos
                    .Where(video => video.ChannelId == channel.Id)
                    .ToDictionaryAsync(video => video.YouTubeVideoId, StringComparer.OrdinalIgnoreCase, cancellationToken);

                var latestVideos = await GetLatestVideosAsync(channel.ChannelId, cancellationToken);
                foreach (var latestVideo in latestVideos)
                {
                    if (existingVideos.TryGetValue(latestVideo.YouTubeVideoId, out var existing))
                    {
                        existing.Title = latestVideo.Title;
                        existing.Url = latestVideo.Url;
                        existing.ThumbnailUrl = latestVideo.ThumbnailUrl;
                        existing.Summary = latestVideo.Summary;
                        existing.ChannelTitle = latestVideo.ChannelTitle;
                        existing.PublishedUtc = latestVideo.PublishedUtc;
                        existing.LastSyncedUtc = now;
                        existing.UpdatedUtc = now;
                    }
                    else
                    {
                        latestVideo.ChannelId = channel.Id;
                        latestVideo.CreatedUtc = now;
                        latestVideo.UpdatedUtc = now;
                        latestVideo.LastSyncedUtc = now;
                        db.Videos.Add(latestVideo);
                    }

                    upsertedVideos++;
                }

                refreshedChannels++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to refresh YouTube videos for channel {Channel}", channel.DisplayName);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new YouTubeSyncResult(true, refreshedChannels, upsertedVideos, $"Refreshed {refreshedChannels} channel(s) and upserted {upsertedVideos} video item(s).");
    }

    private async Task<ResolvedYouTubeChannel?> ResolveStoredChannelAsync(VideoChannel channel, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(channel.ChannelId) && !channel.ChannelId.StartsWith("seed:", StringComparison.OrdinalIgnoreCase))
        {
            return await GetChannelByIdAsync(channel.ChannelId, cancellationToken);
        }

        var channelId = await ResolveChannelIdFromInputAsync(channel.ChannelUrl, cancellationToken)
            ?? await ResolveChannelIdFromInputAsync(channel.Handle, cancellationToken);

        return string.IsNullOrWhiteSpace(channelId)
            ? null
            : await GetChannelByIdAsync(channelId, cancellationToken);
    }

    private async Task<ResolvedYouTubeChannel?> GetChannelByIdAsync(string channelId, CancellationToken cancellationToken)
    {
        var requestUri = $"https://www.googleapis.com/youtube/v3/channels?part=snippet&id={Uri.EscapeDataString(channelId)}&key={Uri.EscapeDataString(settings.ApiKey)}";
        using var response = await httpClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);
        var item = document.RootElement.GetProperty("items").EnumerateArray().FirstOrDefault();
        if (item.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        var snippet = item.GetProperty("snippet");
        var title = snippet.GetProperty("title").GetString() ?? channelId;
        var description = snippet.TryGetProperty("description", out var descriptionElement)
            ? descriptionElement.GetString() ?? string.Empty
            : string.Empty;
        var customUrl = snippet.TryGetProperty("customUrl", out var customUrlElement)
            ? customUrlElement.GetString() ?? string.Empty
            : string.Empty;
        var handle = NormalizeHandle(customUrl);
        var thumbnailUrl = GetThumbnailUrl(snippet);

        return new ResolvedYouTubeChannel(
            item.GetProperty("id").GetString() ?? channelId,
            title,
            handle,
            string.IsNullOrWhiteSpace(handle) ? $"https://www.youtube.com/channel/{channelId}" : $"https://www.youtube.com/{handle}",
            description,
            thumbnailUrl);
    }

    private async Task<List<VideoEntry>> GetLatestVideosAsync(string channelId, CancellationToken cancellationToken)
    {
        var requestUri = $"https://www.googleapis.com/youtube/v3/search?part=snippet&channelId={Uri.EscapeDataString(channelId)}&maxResults={settings.MaxVideosPerChannel}&order=date&type=video&key={Uri.EscapeDataString(settings.ApiKey)}";
        using var response = await httpClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);

        var videos = new List<VideoEntry>();
        foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
        {
            var idNode = item.GetProperty("id");
            var videoId = idNode.TryGetProperty("videoId", out var videoIdElement)
                ? videoIdElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(videoId))
            {
                continue;
            }

            var snippet = item.GetProperty("snippet");
            _ = DateTime.TryParse(snippet.GetProperty("publishedAt").GetString(), out var publishedUtc);
            var title = snippet.GetProperty("title").GetString() ?? videoId;
            var summary = snippet.TryGetProperty("description", out var summaryElement)
                ? summaryElement.GetString() ?? string.Empty
                : string.Empty;
            var channelTitle = snippet.TryGetProperty("channelTitle", out var channelTitleElement)
                ? channelTitleElement.GetString() ?? string.Empty
                : string.Empty;

            videos.Add(new VideoEntry
            {
                YouTubeVideoId = videoId,
                Title = Truncate(title, 160),
                Url = $"https://www.youtube.com/watch?v={videoId}",
                ThumbnailUrl = GetThumbnailUrl(snippet),
                Summary = Truncate(summary, 2000),
                ChannelTitle = Truncate(channelTitle, 120),
                PublishedUtc = publishedUtc == default ? DateTime.UtcNow : publishedUtc.ToUniversalTime(),
                WatchState = VideoWatchState.Inbox
            });
        }

        return videos;
    }

    private async Task<string?> ResolveChannelIdFromInputAsync(string? input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var trimmed = input.Trim();
        var directChannelId = TryExtractChannelId(trimmed);
        if (!string.IsNullOrWhiteSpace(directChannelId))
        {
            return directChannelId;
        }

        var handle = ExtractHandle(trimmed);
        if (!string.IsNullOrWhiteSpace(handle))
        {
            var pageUrl = $"https://www.youtube.com/{NormalizeHandle(handle)}";
            return await ResolveChannelIdFromPageAsync(pageUrl, cancellationToken);
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var channelUri))
        {
            return await ResolveChannelIdFromPageAsync(channelUri.ToString(), cancellationToken);
        }

        return null;
    }

    private async Task<string?> ResolveChannelIdFromPageAsync(string pageUrl, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(pageUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var match = YouTubeChannelIdRegex().Match(html);
        return match.Success ? match.Groups["channelId"].Value : null;
    }

    private static string GetThumbnailUrl(JsonElement snippet)
    {
        if (!snippet.TryGetProperty("thumbnails", out var thumbnails))
        {
            return string.Empty;
        }

        foreach (var thumbnailKey in new[] { "high", "medium", "default" })
        {
            if (thumbnails.TryGetProperty(thumbnailKey, out var thumbnail) &&
                thumbnail.TryGetProperty("url", out var urlElement))
            {
                return urlElement.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static string? TryExtractChannelId(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        if (input.StartsWith("UC", StringComparison.OrdinalIgnoreCase) && input.Length >= 20)
        {
            return input.Trim();
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length >= 2 && string.Equals(segments[0], "channel", StringComparison.OrdinalIgnoreCase)
            ? segments[1]
            : null;
    }

    private static string ExtractHandle(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        if (input.StartsWith('@'))
        {
            return NormalizeHandle(input);
        }

        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            var segment = uri.AbsolutePath.Trim('/');
            if (segment.StartsWith('@'))
            {
                return NormalizeHandle(segment);
            }
        }

        return string.Empty;
    }

    private static string NormalizeHandle(string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return string.Empty;
        }

        var trimmed = handle.Trim().TrimEnd('/');
        return trimmed.StartsWith('@') ? trimmed : $"@{trimmed}";
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    [GeneratedRegex("\"(?:externalId|channelId|browseId)\"\\s*:\\s*\"(?<channelId>UC[0-9A-Za-z_-]{20,})\"")]
    private static partial Regex YouTubeChannelIdRegex();
}

public record ResolvedYouTubeChannel(
    string ChannelId,
    string DisplayName,
    string Handle,
    string ChannelUrl,
    string Description,
    string ThumbnailUrl);

public record YouTubeChannelResolutionResult(bool IsSuccess, string Message, ResolvedYouTubeChannel? Channel)
{
    public static YouTubeChannelResolutionResult Success(ResolvedYouTubeChannel channel)
        => new(true, string.Empty, channel);

    public static YouTubeChannelResolutionResult Failure(string message)
        => new(false, message, null);
}

public record YouTubeSyncResult(bool IsSuccess, int ChannelsRefreshed, int VideosUpserted, string Message);