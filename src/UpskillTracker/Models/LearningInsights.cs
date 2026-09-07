namespace UpskillTracker.Models;

public sealed record TopicProgress(
    string Domain,
    int Total,
    int Completed,
    int InProgress,
    decimal ProgressPercent);

public sealed record LearningDay(DateTime Date, int Activities, bool IsFuture);

public enum RecallConfidence
{
    Revisit = 1,
    Explain = 2,
    Apply = 3
}

public sealed record RecallReview(TrainingItem Item, DateTime DueDate, DateTime? LastReviewedDate);

public sealed class LearningStudioSession
{
    public int SessionMinutes { get; set; } = 30;
    public int SelectedReviewId { get; set; }
    public bool IsSaving { get; set; }
    public string? SavedMessage { get; set; }
    public Dictionary<int, RecallDraft> Drafts { get; } = [];
    public event Func<Task>? RefreshRequested;

    public void CompleteDraft(int itemId, RecallDraft submittedDraft)
    {
        if (Drafts.TryGetValue(itemId, out var current) && ReferenceEquals(current, submittedDraft))
        {
            Drafts.Remove(itemId);
        }
    }

    public async Task RefreshAsync()
    {
        var handlers = RefreshRequested?.GetInvocationList() ?? [];
        foreach (var refresh in handlers.Cast<Func<Task>>())
        {
            await refresh();
        }
    }
}

public sealed class RecallDraft
{
    public string Answer { get; set; } = string.Empty;
    public string NextStep { get; set; } = string.Empty;
    public RecallConfidence? Confidence { get; set; }
}
