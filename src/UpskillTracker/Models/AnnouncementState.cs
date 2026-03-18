using System.ComponentModel.DataAnnotations;

namespace UpskillTracker.Models;

public class AnnouncementState
{
    [Key]
    [MaxLength(500)]
    public string Url { get; set; } = string.Empty;

    public AnnouncementStream Stream { get; set; }

    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(120)]
    public string Source { get; set; } = string.Empty;

    [MaxLength(120)]
    public string Topic { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string Summary { get; set; } = string.Empty;

    public DateTime PublishedUtc { get; set; }

    public bool IsSeen { get; set; }

    public bool IsSavedToResources { get; set; }

    public DateTime? FirstSeenUtc { get; set; }

    public DateTime? LastSeenUtc { get; set; }

    public DateTime? LastOpenedUtc { get; set; }

    public DateTime? SavedToResourcesUtc { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}