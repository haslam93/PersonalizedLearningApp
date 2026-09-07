using UpskillTracker.Models;

namespace UpskillTracker.Services;

public static class LearningInsightsBuilder
{
    public const string RecallCategory = "Active recall";
    public const int MaxAnswerLength = 2800;
    public const int MaxNextStepLength = 600;

    public static IReadOnlyList<TopicProgress> BuildTopics(IEnumerable<TrainingItem> items)
        => items
            .GroupBy(item => item.Domain.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new TopicProgress(
                group.Key,
                group.Count(),
                group.Count(item => item.Status == TrackerStatus.Completed),
                group.Count(item => item.Status == TrackerStatus.InProgress),
                Math.Round(group.Average(item => item.Status == TrackerStatus.Completed
                    ? 100m
                    : Math.Clamp(item.ProgressPercent, 0, 100)), 0)))
            .OrderByDescending(topic => topic.InProgress)
            .ThenByDescending(topic => topic.Total - topic.Completed)
            .ThenBy(topic => topic.Domain, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static IReadOnlyList<LearningDay> BuildWeek(
        IEnumerable<LearningActivity> activities, DateTime utcNow, TimeZoneInfo timeZone)
    {
        var today = LocalDate(utcNow, timeZone);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var counts = activities
            .Where(activity => activity.OccurredUtc <= utcNow)
            .GroupBy(activity => LocalDate(activity.OccurredUtc, timeZone))
            .ToDictionary(group => group.Key, group => group.Count());

        return Enumerable.Range(0, 7)
            .Select(offset => monday.AddDays(offset))
            .Select(date => new LearningDay(date, counts.GetValueOrDefault(date), date > today))
            .ToList();
    }

    public static IReadOnlyList<RecallReview> BuildReviewQueue(
        IEnumerable<TrainingItem> items, IEnumerable<NoteEntry> notes, DateTime utcNow, TimeZoneInfo timeZone)
    {
        var today = LocalDate(utcNow, timeZone);
        var recallNotes = notes
            .Where(note => note.Category.Equals(RecallCategory, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(note => note.CreatedUtc)
            .ToList();

        return items
            .Where(item => item.Status is TrackerStatus.Completed or TrackerStatus.InProgress)
            .Select(item =>
            {
                var lastReview = recallNotes.FirstOrDefault(note => HasTag(note, $"training:{item.Id}"));
                var reviewedDate = lastReview is null ? (DateTime?)null : LocalDate(lastReview.CreatedUtc, timeZone);
                var due = lastReview is not null && reviewedDate is DateTime date
                    ? date.AddDays(GetReviewInterval(GetConfidence(lastReview)))
                    : today;
                return new RecallReview(item, due, reviewedDate);
            })
            .OrderBy(review => review.DueDate)
            .ThenBy(review => review.LastReviewedDate.HasValue)
            .ThenByDescending(review => review.Item.Status == TrackerStatus.Completed)
            .ThenBy(review => review.Item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static NoteEntry CreateReflection(
        TrainingItem item, string answer, string nextStep, RecallConfidence confidence)
    {
        if (item.Id <= 0)
        {
            throw new ArgumentException("Choose a saved plan item to review.", nameof(item));
        }

        if (string.IsNullOrWhiteSpace(answer) || answer.Trim().Length > MaxAnswerLength)
        {
            throw new ArgumentException($"Write a reflection of 1 to {MaxAnswerLength} characters.", nameof(answer));
        }

        if (nextStep.Trim().Length > MaxNextStepLength)
        {
            throw new ArgumentException($"Keep the next step to {MaxNextStepLength} characters.", nameof(nextStep));
        }

        var label = confidence switch
        {
            RecallConfidence.Revisit => "Needs another pass",
            RecallConfidence.Explain => "Can explain it",
            RecallConfidence.Apply => "Can apply it",
            _ => throw new ArgumentOutOfRangeException(nameof(confidence))
        };
        var title = $"Recall: {item.Title}";

        return new NoteEntry
        {
            Title = title.Length <= 140 ? title : title[..140],
            Category = RecallCategory,
            RelatedArea = item.Domain,
            Tags = $"active-recall, training:{item.Id}, confidence:{(int)confidence}",
            Content = $"From memory:\n{answer.Trim()}\n\nSelf-assessment: {label}\n\nNext step:\n{(string.IsNullOrWhiteSpace(nextStep) ? "Revisit the saved context and try again at the next review." : nextStep.Trim())}"
        };
    }

    public static int GetReviewInterval(RecallConfidence confidence) => confidence switch
    {
        RecallConfidence.Revisit => 1,
        RecallConfidence.Explain => 3,
        RecallConfidence.Apply => 7,
        _ => throw new ArgumentOutOfRangeException(nameof(confidence))
    };

    public static decimal RemainingHours(TrainingItem item)
        => item.Status == TrackerStatus.Completed
            ? 0
            : Math.Round(Math.Max(0, item.EstimatedHours) * (100 - Math.Clamp(item.ProgressPercent, 0, 100)) / 100, 1);

    private static RecallConfidence GetConfidence(NoteEntry note)
        => Enum.GetValues<RecallConfidence>()
            .FirstOrDefault(confidence => HasTag(note, $"confidence:{(int)confidence}"), RecallConfidence.Revisit);

    private static bool HasTag(NoteEntry note, string tag)
        => note.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(tag, StringComparer.OrdinalIgnoreCase);

    private static DateTime LocalDate(DateTime utc, TimeZoneInfo timeZone)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone).Date;
}
