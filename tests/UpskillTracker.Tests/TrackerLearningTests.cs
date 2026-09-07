using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using UpskillTracker.Data;
using UpskillTracker.Models;
using UpskillTracker.Services;
using Xunit;

namespace UpskillTracker.Tests;

public sealed class TrackerLearningTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private ContextFactory factory = null!;
    private TrackerService tracker = null!;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        factory = new ContextFactory(new DbContextOptionsBuilder<TrackerDbContext>().UseSqlite(connection).Options);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        tracker = new TrackerService(factory, NullLogger<TrackerService>.Instance, new DatabaseAvailabilityState());
    }

    public async Task DisposeAsync() => await connection.DisposeAsync();

    [Theory]
    [InlineData(VideoWatchState.Inbox, VideoWatchState.NeedToWatch)]
    [InlineData(VideoWatchState.Removed, VideoWatchState.NeedToWatch)]
    [InlineData(VideoWatchState.NeedToWatch, VideoWatchState.NeedToWatch)]
    [InlineData(VideoWatchState.Seen, VideoWatchState.Seen)]
    public async Task OpeningAVideoIsNotProofOfWatching(VideoWatchState before, VideoWatchState after)
    {
        var id = await AddVideoAsync(before);
        await tracker.MarkVideoOpenedAsync(id);
        var video = Assert.Single(await tracker.GetVideosAsync());
        Assert.Equal(after, video.WatchState);
        Assert.NotNull(video.LastViewedUtc);
        Assert.Null(video.RemovedUtc);
        Assert.Empty((await tracker.GetLearningHistoryAsync()).Activities);
    }

    [Fact]
    public async Task ExplicitWatchCompletionRecordsOneDailyActivity()
    {
        var id = await AddVideoAsync(VideoWatchState.NeedToWatch);
        await tracker.UpdateVideoWatchStateAsync(id, VideoWatchState.Seen);
        await tracker.UpdateVideoWatchStateAsync(id, VideoWatchState.Seen);

        var activity = Assert.Single((await tracker.GetLearningHistoryAsync()).Activities);
        Assert.Equal(LearningActivityType.VideoWatched, activity.Type);
        Assert.Equal(id, activity.SourceId);
    }

    [Fact]
    public async Task RecallPersistsInNotesAndHistoryWithoutChangingPlanProgress()
    {
        var item = new TrainingItem { Title = "Explain OneLake", Domain = "Fabric", Status = TrackerStatus.InProgress, ProgressPercent = 40 };
        await tracker.SaveTrainingItemAsync(item);
        var note = LearningInsightsBuilder.CreateReflection(item, "OneLake is the logical data lake.", "Try a shortcut.", RecallConfidence.Explain);
        await tracker.SaveNoteAsync(note);

        var saved = Assert.Single(await tracker.GetNotesAsync());
        Assert.Contains("OneLake is the logical data lake.", saved.Content);
        Assert.Contains($"training:{item.Id}", saved.Tags);
        Assert.Single((await tracker.GetLearningHistoryAsync()).Activities, activity => activity.Type == LearningActivityType.ReflectionAdded);
        Assert.Equal(40, Assert.Single(await tracker.GetTrainingItemsAsync()).ProgressPercent);
        var review = Assert.Single(LearningInsightsBuilder.BuildReviewQueue([item], [saved], DateTime.UtcNow, TimeZoneInfo.Utc));
        Assert.Equal(saved.CreatedUtc.Date.AddDays(3), review.DueDate);
    }

    [Fact]
    public async Task DeletingANotePreservesItsLearningHistory()
    {
        var note = new NoteEntry { Title = "A useful insight", Content = "Explain the trade-off before choosing a service." };
        await tracker.SaveNoteAsync(note);
        await tracker.DeleteNoteAsync(note.Id);
        Assert.Empty(await tracker.GetNotesAsync());
        Assert.Single((await tracker.GetLearningHistoryAsync()).Activities);
    }

    [Fact]
    public async Task CompletionNormalizesProgressAndIsNotDoubleCountedOnResave()
    {
        var item = new TrainingItem { Title = "A completed lab", Domain = "Fabric", Status = TrackerStatus.Completed, ProgressPercent = 5 };
        await tracker.SaveTrainingItemAsync(item);
        await tracker.SaveTrainingItemAsync(item);
        Assert.Equal(100, Assert.Single(await tracker.GetTrainingItemsAsync()).ProgressPercent);
        Assert.Single((await tracker.GetLearningHistoryAsync()).Activities);
    }

    private async Task<int> AddVideoAsync(VideoWatchState state)
    {
        await using var db = factory.CreateDbContext();
        var channel = new VideoChannel { DisplayName = "Test channel", Handle = "@fixture", ChannelId = "fixture-channel" };
        db.VideoChannels.Add(channel);
        await db.SaveChangesAsync();
        var video = new VideoEntry
        {
            ChannelId = channel.Id,
            YouTubeVideoId = "fixture-video",
            Title = "A learning video",
            Url = "https://www.youtube.com/watch?v=fixture-video",
            WatchState = state,
            RemovedUtc = state == VideoWatchState.Removed ? DateTime.UtcNow : null
        };
        db.Videos.Add(video);
        await db.SaveChangesAsync();
        return video.Id;
    }

    private sealed class ContextFactory(DbContextOptions<TrackerDbContext> options) : IDbContextFactory<TrackerDbContext>
    {
        public TrackerDbContext CreateDbContext() => new(options);
        public Task<TrackerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
