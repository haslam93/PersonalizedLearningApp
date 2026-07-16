namespace UpskillTracker.Models;

public class LearningHistorySnapshot
{
    public int TotalActivities { get; init; }

    public int ActiveDays { get; init; }

    public int ActiveDaysLast30 { get; init; }

    public int CompletedMilestones { get; init; }

    public int ThingsExplored { get; init; }

    public int Reflections { get; init; }

    public decimal CompletedHours { get; init; }

    public int CurrentMonthActivities { get; init; }

    public string BestMonthLabel { get; init; } = "No activity yet";

    public int BestMonthActivities { get; init; }

    public DateTime? FirstActivityUtc { get; init; }

    public DateTime? LastActivityUtc { get; init; }

    public IReadOnlyList<LearningActivity> Activities { get; init; } = [];

    public IReadOnlyList<LearningActivity> RecentActivities { get; init; } = [];
}
