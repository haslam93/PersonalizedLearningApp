using System.ComponentModel.DataAnnotations;
using UpskillTracker.Models;
using UpskillTracker.Services;
using Xunit;

namespace UpskillTracker.Tests;

public class LearningInsightsTests
{
    private static readonly DateTime Now = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo EasternOffset = TimeZoneInfo.CreateCustomTimeZone("Test Eastern", TimeSpan.FromHours(-4), "Test Eastern", "Test Eastern");

    [Fact]
    public void TopicMapGroupsCaseAndWhitespaceAndHonorsCompletionStatus()
    {
        var result = LearningInsightsBuilder.BuildTopics([
            Item(1, " Fabric ", TrackerStatus.Completed, 0),
            Item(2, "fabric", TrackerStatus.InProgress, 40),
            Item(3, "GitHub", TrackerStatus.NotStarted)
        ]);

        var topic = result[0];
        Assert.Equal("Fabric", topic.Domain);
        Assert.Equal(2, topic.Total);
        Assert.Equal(1, topic.Completed);
        Assert.Equal(1, topic.InProgress);
        Assert.Equal(70m, topic.ProgressPercent);
    }

    [Fact]
    public void TopicProgressCannotEscapeZeroToOneHundred()
    {
        var topic = Assert.Single(LearningInsightsBuilder.BuildTopics([
            Item(1, progress: -20), Item(2, progress: 150)
        ]));
        Assert.Equal(50m, topic.ProgressPercent);
        Assert.Empty(LearningInsightsBuilder.BuildTopics([]));
    }

    [Fact]
    public void WeekUsesBrowserLocalDatesAndStartsOnMonday()
    {
        var now = new DateTime(2026, 9, 7, 1, 0, 0, DateTimeKind.Utc);
        var week = LearningInsightsBuilder.BuildWeek([
            new() { OccurredUtc = now.AddHours(-1) },
            new() { OccurredUtc = now.AddDays(-6) },
            new() { OccurredUtc = now.AddDays(1) }
        ], now, EasternOffset);

        Assert.Equal(new DateTime(2026, 8, 31), week[0].Date);
        Assert.Equal(new DateTime(2026, 9, 6), week[6].Date);
        Assert.Equal(1, week[6].Activities);
        Assert.Equal(2, week.Sum(day => day.Activities));
        Assert.DoesNotContain(week, day => day.IsFuture);
    }

    [Fact]
    public void FutureDaysAreNotShownAsMissedLearningDays()
    {
        var week = LearningInsightsBuilder.BuildWeek([], Now, TimeZoneInfo.Utc);
        Assert.False(week[0].IsFuture);
        Assert.All(week.Skip(1), day => Assert.True(day.IsFuture));
        Assert.All(week, day => Assert.Equal(0, day.Activities));
    }

    [Theory]
    [InlineData(RecallConfidence.Revisit, 1)]
    [InlineData(RecallConfidence.Explain, 3)]
    [InlineData(RecallConfidence.Apply, 7)]
    public void ConfidenceSchedulesTheDocumentedInterval(RecallConfidence confidence, int days)
    {
        var item = Item(12, status: TrackerStatus.Completed);
        var note = LearningInsightsBuilder.CreateReflection(item, "My explanation", "Try a small example", confidence);
        note.CreatedUtc = Now;

        var review = Assert.Single(LearningInsightsBuilder.BuildReviewQueue([item], [note], Now, TimeZoneInfo.Utc));
        Assert.Equal(Now.Date.AddDays(days), review.DueDate);
        Assert.Equal(Now.Date, review.LastReviewedDate);
    }

    [Fact]
    public void ReviewQueueOnlyIncludesStartedOrCompletedWork()
    {
        var queue = LearningInsightsBuilder.BuildReviewQueue([
            Item(1, status: TrackerStatus.NotStarted),
            Item(2, status: TrackerStatus.Blocked),
            Item(3, status: TrackerStatus.InProgress),
            Item(4, status: TrackerStatus.Completed)
        ], [], Now, TimeZoneInfo.Utc);

        Assert.Equal(new[] { 4, 3 }, queue.Select(review => review.Item.Id));
        Assert.All(queue, review => Assert.Equal(Now.Date, review.DueDate));
        Assert.All(queue, review => Assert.Null(review.LastReviewedDate));
    }

    [Fact]
    public void ReviewTagsMatchWholeIdentifiersNotPrefixes()
    {
        var note = LearningInsightsBuilder.CreateReflection(Item(123), "Saved recall", "", RecallConfidence.Apply);
        note.CreatedUtc = Now;
        var item = Item(12, status: TrackerStatus.InProgress);
        var review = Assert.Single(LearningInsightsBuilder.BuildReviewQueue([item], [note], Now, TimeZoneInfo.Utc));
        Assert.Null(review.LastReviewedDate);
        Assert.Equal(Now.Date, review.DueDate);
    }

    [Fact]
    public void LatestReviewWinsButEditingAnOldNoteDoesNotRescheduleIt()
    {
        var item = Item(1, status: TrackerStatus.Completed);
        var older = LearningInsightsBuilder.CreateReflection(item, "Old reflection", "", RecallConfidence.Apply);
        older.CreatedUtc = Now.AddDays(-4);
        older.UpdatedUtc = Now.AddDays(1);
        var newer = LearningInsightsBuilder.CreateReflection(item, "Still need practice", "", RecallConfidence.Revisit);
        newer.CreatedUtc = Now.AddDays(-1);

        var review = Assert.Single(LearningInsightsBuilder.BuildReviewQueue([item], [older, newer], Now, TimeZoneInfo.Utc));
        Assert.Equal(Now.Date, review.DueDate);
    }

    [Fact]
    public void ReviewDueDatesUseTheLearnersCalendarDay()
    {
        var item = Item(1, status: TrackerStatus.Completed);
        var note = LearningInsightsBuilder.CreateReflection(item, "Late night reflection", "", RecallConfidence.Explain);
        note.CreatedUtc = new DateTime(2026, 9, 7, 2, 0, 0, DateTimeKind.Utc);
        var review = Assert.Single(LearningInsightsBuilder.BuildReviewQueue([item], [note], Now, EasternOffset));
        Assert.Equal(new DateTime(2026, 9, 9), review.DueDate);
    }

    [Fact]
    public void MissingConfidenceUsesTheShortestReviewInterval()
    {
        var item = Item(1, status: TrackerStatus.Completed);
        var note = new NoteEntry { Category = "Active recall", Tags = "training:1", CreatedUtc = Now };
        var review = Assert.Single(LearningInsightsBuilder.BuildReviewQueue([item], [note], Now, TimeZoneInfo.Utc));
        Assert.Equal(Now.Date.AddDays(1), review.DueDate);
    }

    [Fact]
    public void MaxLengthReflectionFitsTheExistingSchemaWithoutTruncatingTheAnswer()
    {
        var item = Item(1);
        item.Title = new string('T', 140);
        var note = LearningInsightsBuilder.CreateReflection(item, new string('A', 2800), new string('N', 600), RecallConfidence.Apply);
        var validation = new List<ValidationResult>();

        Assert.True(Validator.TryValidateObject(note, new ValidationContext(note), validation, true));
        Assert.Equal(140, note.Title.Length);
        Assert.Contains(new string('A', 2800), note.Content);
        Assert.True(note.Content.Length <= 4000);
        Assert.Equal("Active recall", note.Category);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyAnswersAreRejected(string answer)
        => Assert.Throws<ArgumentException>(() => LearningInsightsBuilder.CreateReflection(Item(1), answer, "", RecallConfidence.Revisit));

    [Fact]
    public void InvalidReflectionInputsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => LearningInsightsBuilder.CreateReflection(Item(0), "A", "", RecallConfidence.Revisit));
        Assert.Throws<ArgumentException>(() => LearningInsightsBuilder.CreateReflection(Item(1), new string('A', 2801), "", RecallConfidence.Revisit));
        Assert.Throws<ArgumentException>(() => LearningInsightsBuilder.CreateReflection(Item(1), "A", new string('N', 601), RecallConfidence.Revisit));
        Assert.Throws<ArgumentOutOfRangeException>(() => LearningInsightsBuilder.GetReviewInterval((RecallConfidence)0));
    }

    [Theory]
    [InlineData(TrackerStatus.NotStarted, 0, 2)]
    [InlineData(TrackerStatus.InProgress, 50, 1)]
    [InlineData(TrackerStatus.Completed, 0, 0)]
    [InlineData(TrackerStatus.InProgress, 110, 0)]
    public void RemainingEffortAccountsForProgress(TrackerStatus status, int progress, int hours)
        => Assert.Equal(hours, LearningInsightsBuilder.RemainingHours(Item(1, status: status, progress: progress)));

    [Fact]
    public void OptionalOverdueWorkDoesNotCrowdOutCoreLearning()
    {
        var optional = Item(1);
        optional.Lane = LearningLane.Stretch;
        optional.TargetDate = Now.AddDays(-30);
        var core = Item(2);
        core.TargetDate = Now.AddDays(7);
        Assert.False(TrainingPlanPrioritizer.IsAtRisk(optional, Now));
        Assert.Equal(core.Id, TrainingPlanPrioritizer.OrderForFocus([optional, core], Now).First().Id);
    }

    [Fact]
    public void CompletingAPendingSaveCannotEraseAnotherTopicsDraft()
    {
        var session = new LearningStudioSession { SelectedReviewId = 1, IsSaving = true };
        var submitted = new RecallDraft { Answer = "Submitted topic A" };
        var other = new RecallDraft { Answer = "Unsaved topic B" };
        session.Drafts[1] = submitted;
        session.Drafts[2] = other;
        session.SelectedReviewId = 2;
        session.CompleteDraft(1, submitted);
        Assert.Same(other, session.Drafts[2]);
        Assert.Equal("Unsaved topic B", session.Drafts[2].Answer);
        Assert.False(session.Drafts.ContainsKey(1));
    }

    [Fact]
    public void CompletingAnOldSaveDoesNotRemoveANewerDraftForTheSameTopic()
    {
        var session = new LearningStudioSession();
        var submitted = new RecallDraft { Answer = "Submitted" };
        var replacement = new RecallDraft { Answer = "New thought" };
        session.Drafts[1] = replacement;
        session.CompleteDraft(1, submitted);
        Assert.Same(replacement, session.Drafts[1]);
    }

    private static TrainingItem Item(int id, string domain = "Fabric", TrackerStatus status = TrackerStatus.InProgress, int progress = 0)
        => new() { Id = id, Domain = domain, Title = $"Learning step {id}", Status = status, ProgressPercent = progress, EstimatedHours = 2 };
}
