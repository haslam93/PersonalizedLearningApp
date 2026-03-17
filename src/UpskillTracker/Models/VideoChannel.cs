using System.ComponentModel.DataAnnotations;

namespace UpskillTracker.Models;

public class VideoChannel
{
    public int Id { get; set; }

    [Required]
    [MaxLength(80)]
    public string DisplayName { get; set; } = string.Empty;

    [Required]
    [MaxLength(120)]
    public string Handle { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string ChannelId { get; set; } = string.Empty;

    [Url]
    [MaxLength(500)]
    public string ChannelUrl { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Description { get; set; } = string.Empty;

    [Url]
    [MaxLength(500)]
    public string ThumbnailUrl { get; set; } = string.Empty;

    public bool IsSeeded { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastSyncedUtc { get; set; }

    public ICollection<VideoEntry> Videos { get; set; } = [];

    public VideoChannel Clone()
    {
        return (VideoChannel)MemberwiseClone();
    }
}