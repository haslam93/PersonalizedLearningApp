using UpskillTracker.Models;

namespace UpskillTracker.Services;

public static class TrainingPlanPrioritizer
{
    private const int DueSoonWindowDays = 14;

    public static PlanAttentionLevel GetAttentionLevel(TrainingItem item, DateTime today)
    {
        if (item.Status == TrackerStatus.Completed)
        {
            return PlanAttentionLevel.Completed;
        }

        if (IsNiceToHave(item))
        {
            return PlanAttentionLevel.NiceToHave;
        }

        var targetDate = item.TargetDate.Date;
        var normalizedToday = today.Date;

        if (targetDate < normalizedToday)
        {
            return PlanAttentionLevel.Overdue;
        }

        if (targetDate <= normalizedToday.AddDays(DueSoonWindowDays))
        {
            return PlanAttentionLevel.DueSoon;
        }

        if (item.Status == TrackerStatus.InProgress)
        {
            return PlanAttentionLevel.InProgress;
        }

        return PlanAttentionLevel.Planned;
    }

    public static bool IsAtRisk(TrainingItem item, DateTime today)
    {
        var attention = GetAttentionLevel(item, today);
        return attention == PlanAttentionLevel.Overdue ||
            (attention == PlanAttentionLevel.DueSoon && item.ProgressPercent < 50);
    }

    public static bool IsNiceToHave(TrainingItem item)
        => item.Lane == LearningLane.Stretch && !item.ProjectDriven;

    public static string GetCommitmentLabel(TrainingItem item) => item.Lane switch
    {
        LearningLane.RapidRamp => "Rapid ramp",
        LearningLane.Stretch when !item.ProjectDriven => "Nice to have",
        _ => "Core commitment"
    };

    public static string GetAttentionLabel(TrainingItem item, DateTime today)
    {
        var normalizedToday = today.Date;
        var daysUntilDue = (item.TargetDate.Date - normalizedToday).Days;

        return GetAttentionLevel(item, normalizedToday) switch
        {
            PlanAttentionLevel.Overdue => $"Overdue by {Math.Abs(daysUntilDue)} day{(Math.Abs(daysUntilDue) == 1 ? string.Empty : "s")}",
            PlanAttentionLevel.DueSoon when daysUntilDue == 0 => "Due today",
            PlanAttentionLevel.DueSoon when daysUntilDue == 1 => "Due tomorrow",
            PlanAttentionLevel.DueSoon => $"Due in {daysUntilDue} days",
            PlanAttentionLevel.InProgress => "In progress",
            PlanAttentionLevel.NiceToHave => "Nice to have",
            PlanAttentionLevel.Completed => "Completed",
            _ => "Planned"
        };
    }

    public static int GetFocusRank(TrainingItem item, DateTime today)
    {
        var attentionRank = GetAttentionLevel(item, today) switch
        {
            PlanAttentionLevel.Overdue => 0,
            PlanAttentionLevel.DueSoon => 1,
            PlanAttentionLevel.InProgress => 2,
            PlanAttentionLevel.Planned => 3,
            PlanAttentionLevel.NiceToHave => 5,
            PlanAttentionLevel.Completed => 6,
            _ => 4
        };

        if (attentionRank == 3 && item.ProjectDriven)
        {
            return 2;
        }

        return attentionRank;
    }

    public static IOrderedEnumerable<TrainingItem> OrderForFocus(IEnumerable<TrainingItem> items, DateTime today)
        => items
            .OrderBy(item => GetFocusRank(item, today))
            .ThenByDescending(item => item.Status == TrackerStatus.InProgress)
            .ThenBy(item => item.TargetDate)
            .ThenByDescending(item => item.Priority);
}
