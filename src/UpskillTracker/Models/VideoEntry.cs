using System.ComponentModel.DataAnnotations;

namespace UpskillTracker.Models;

public class VideoEntry
{
    public int Id { get; set; }

    public int ChannelId { get; set; }

    public VideoChannel? Channel { get; set; }

    [Required]
    [MaxLength(32)]
    public string YouTubeVideoId { get; set; } = string.Empty;

    [Required]
    [MaxLength(160)]
    public string Title { get; set; } = string.Empty;

    [Url]
    [MaxLength(500)]
    public string Url { get; set; } = string.Empty;

    [Url]
    [MaxLength(500)]
    public string ThumbnailUrl { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string Summary { get; set; } = string.Empty;

    [MaxLength(120)]
    public string ChannelTitle { get; set; } = string.Empty;

    public DateTime PublishedUtc { get; set; }

    public VideoWatchState WatchState { get; set; } = VideoWatchState.Inbox;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastViewedUtc { get; set; }

    public DateTime? LastSyncedUtc { get; set; }

    public DateTime? RemovedUtc { get; set; }

    public VideoEntry Clone()
    {
        return (VideoEntry)MemberwiseClone();
    }
}