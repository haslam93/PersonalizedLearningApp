using System.ComponentModel.DataAnnotations;

namespace UpskillTracker.Models;

public class TrainingItem
{
    public int Id { get; set; }

    [Required]
    [MaxLength(140)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [MaxLength(80)]
    public string Domain { get; set; } = string.Empty;

    [MaxLength(80)]
    public string Category { get; set; } = string.Empty;

    [MaxLength(1500)]
    public string Description { get; set; } = string.Empty;

    public DateTime TargetDate { get; set; } = DateTime.Today.AddDays(7);

    public TrackerStatus Status { get; set; } = TrackerStatus.NotStarted;

    public LearningLane Lane { get; set; } = LearningLane.Core;

    public TrainingItemType Type { get; set; } = TrainingItemType.Learning;

    [Range(0, 100)]
    public int ProgressPercent { get; set; }

    [Range(0.5, 40)]
    public decimal EstimatedHours { get; set; } = 2;

    [Range(1, 5)]
    public int Priority { get; set; } = 3;

    public bool ProjectDriven { get; set; }

    [MaxLength(4000)]
    public string Notes { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Evidence { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastStatusChangedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public TrainingItem Clone()
    {
        return (TrainingItem)MemberwiseClone();
    }
}
