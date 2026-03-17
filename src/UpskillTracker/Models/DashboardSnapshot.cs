namespace UpskillTracker.Models;

public class DashboardSnapshot
{
    public int TotalItems { get; init; }

    public int CompletedItems { get; init; }

    public int InProgressItems { get; init; }

    public int OverdueItems { get; init; }

    public int DueThisMonth { get; init; }

    public int RapidRampItems { get; init; }

    public decimal CompletionRate { get; init; }

    public IReadOnlyList<TrainingItem> UpcomingItems { get; init; } = [];

    public IReadOnlyList<TrainingItem> FocusItems { get; init; } = [];

    public IReadOnlyList<ResourceEntry> PinnedResources { get; init; } = [];

    public IReadOnlyList<NoteEntry> RecentNotes { get; init; } = [];

    public IReadOnlyList<VideoEntry> NeedToWatchVideos { get; init; } = [];
}
