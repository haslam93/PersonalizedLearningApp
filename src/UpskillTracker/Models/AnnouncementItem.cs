namespace UpskillTracker.Models;

public class AnnouncementItem
{
    public string Title { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public DateTime PublishedUtc { get; init; }

    public string Source { get; init; } = string.Empty;

    public string Topic { get; init; } = string.Empty;

    public string SourceUrl { get; init; } = string.Empty;
}