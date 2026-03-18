using System.ComponentModel.DataAnnotations;

namespace UpskillTracker.Models;

public class AppMetadataEntry
{
    [Key]
    [MaxLength(120)]
    public string Key { get; set; } = string.Empty;

    [MaxLength(4000)]
    public string Value { get; set; } = string.Empty;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}