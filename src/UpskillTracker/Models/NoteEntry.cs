using System.ComponentModel.DataAnnotations;

namespace UpskillTracker.Models;

public class NoteEntry
{
    public int Id { get; set; }

    [Required]
    [MaxLength(140)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(80)]
    public string Category { get; set; } = string.Empty;

    [MaxLength(120)]
    public string RelatedArea { get; set; } = string.Empty;

    [MaxLength(300)]
    public string Tags { get; set; } = string.Empty;

    [Required]
    [MaxLength(4000)]
    public string Content { get; set; } = string.Empty;

    public bool IsPinned { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public NoteEntry Clone()
    {
        return (NoteEntry)MemberwiseClone();
    }
}
