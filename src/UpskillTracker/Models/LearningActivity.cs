using System.ComponentModel.DataAnnotations;

namespace UpskillTracker.Models;

public class LearningActivity
{
    public long Id { get; set; }

    public LearningActivityType Type { get; set; }

    [Required]
    [MaxLength(180)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(100)]
    public string Area { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Detail { get; set; } = string.Empty;

    [Required]
    [MaxLength(40)]
    public string SourceKind { get; set; } = string.Empty;

    public int? SourceId { get; set; }

    [Range(0, 100)]
    public int? ProgressPercent { get; set; }

    public decimal? EstimatedHours { get; set; }

    public DateTime OccurredUtc { get; set; } = DateTime.UtcNow;

    [Required]
    [MaxLength(180)]
    public string DeduplicationKey { get; set; } = string.Empty;
}
