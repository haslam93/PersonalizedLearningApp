using System.ComponentModel.DataAnnotations;

namespace UpskillTracker.Models;

public class ResourceEntry
{
    public int Id { get; set; }

    [Required]
    [MaxLength(140)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [MaxLength(80)]
    public string Section { get; set; } = string.Empty;

    [Required]
    [Url]
    [MaxLength(500)]
    public string Url { get; set; } = string.Empty;

    public ResourceKind Kind { get; set; } = ResourceKind.Documentation;

    public bool IsPinned { get; set; }

    [MaxLength(1500)]
    public string Summary { get; set; } = string.Empty;

    [MaxLength(300)]
    public string Tags { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string Notes { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public ResourceEntry Clone()
    {
        return (ResourceEntry)MemberwiseClone();
    }
}
