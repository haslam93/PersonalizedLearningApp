using Microsoft.EntityFrameworkCore;
using UpskillTracker.Data;
using UpskillTracker.Models;

namespace UpskillTracker.Services;

public class TrackerService(IDbContextFactory<TrackerDbContext> dbFactory)
{
    public async Task<DashboardSnapshot> GetDashboardSnapshotAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var today = DateTime.Today;
        var endOfMonth = new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));

        var trainingItems = await db.TrainingItems
            .AsNoTracking()
            .OrderBy(item => item.TargetDate)
            .ThenByDescending(item => item.Priority)
            .ToListAsync();

        var pinnedResources = await db.Resources
            .AsNoTracking()
            .Where(resource => resource.IsPinned)
            .OrderBy(resource => resource.Section)
            .ThenBy(resource => resource.SortOrder)
            .Take(6)
            .ToListAsync();

        var recentNotes = await db.Notes
            .AsNoTracking()
            .OrderByDescending(note => note.UpdatedUtc)
            .Take(4)
            .ToListAsync();

        var needToWatchVideos = await db.Videos
            .AsNoTracking()
            .Where(video => video.WatchState == VideoWatchState.NeedToWatch)
            .OrderByDescending(video => video.PublishedUtc)
            .Take(5)
            .ToListAsync();

        var trackedVideos = await db.Videos
            .AsNoTracking()
            .Where(video => video.WatchState != VideoWatchState.Removed)
            .ToListAsync();

        var completedItems = trainingItems.Count(item => item.Status == TrackerStatus.Completed);
        var inboxVideos = trackedVideos.Count(video => video.WatchState == VideoWatchState.Inbox);
        var queuedVideos = trackedVideos.Count(video => video.WatchState == VideoWatchState.NeedToWatch);
        var seenVideos = trackedVideos.Count(video => video.WatchState == VideoWatchState.Seen);
        var totalTrackedVideos = trackedVideos.Count;
        var upcomingItems = trainingItems.Where(item => item.TargetDate >= today).Take(6).ToList();
        var focusItems = trainingItems
            .Where(item => item.Status != TrackerStatus.Completed)
            .OrderByDescending(item => item.ProjectDriven)
            .ThenBy(item => item.TargetDate)
            .ThenByDescending(item => item.Priority)
            .Take(5)
            .ToList();

        return new DashboardSnapshot
        {
            TotalItems = trainingItems.Count,
            CompletedItems = completedItems,
            InProgressItems = trainingItems.Count(item => item.Status == TrackerStatus.InProgress),
            OverdueItems = trainingItems.Count(item => item.Status != TrackerStatus.Completed && item.TargetDate < today),
            DueThisMonth = trainingItems.Count(item => item.TargetDate >= today && item.TargetDate <= endOfMonth && item.Status != TrackerStatus.Completed),
            RapidRampItems = trainingItems.Count(item => item.Lane == LearningLane.RapidRamp && item.Status != TrackerStatus.Completed),
            CompletionRate = trainingItems.Count == 0 ? 0 : Math.Round((decimal)completedItems / trainingItems.Count * 100, 1),
            TotalTrackedVideos = totalTrackedVideos,
            InboxVideos = inboxVideos,
            NeedToWatchCount = queuedVideos,
            SeenVideos = seenVideos,
            VideoWatchCompletionRate = totalTrackedVideos == 0 ? 0 : Math.Round((decimal)seenVideos / totalTrackedVideos * 100, 1),
            UpcomingItems = upcomingItems,
            FocusItems = focusItems,
            PinnedResources = pinnedResources,
            RecentNotes = recentNotes,
            NeedToWatchVideos = needToWatchVideos
        };
    }

    public async Task<List<TrainingItem>> GetTrainingItemsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.TrainingItems
            .AsNoTracking()
            .OrderBy(item => item.TargetDate)
            .ThenByDescending(item => item.Priority)
            .ToListAsync();
    }

    public async Task<List<ResourceEntry>> GetResourcesAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Resources
            .AsNoTracking()
            .OrderBy(resource => resource.Section)
            .ThenByDescending(resource => resource.IsPinned)
            .ThenBy(resource => resource.SortOrder)
            .ThenBy(resource => resource.Title)
            .ToListAsync();
    }

    public async Task<List<NoteEntry>> GetNotesAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Notes
            .AsNoTracking()
            .OrderByDescending(note => note.IsPinned)
            .ThenByDescending(note => note.UpdatedUtc)
            .ToListAsync();
    }

    public async Task<List<VideoChannel>> GetVideoChannelsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.VideoChannels
            .AsNoTracking()
            .OrderBy(channel => channel.DisplayName)
            .ToListAsync();
    }

    public async Task<List<VideoEntry>> GetVideosAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Videos
            .AsNoTracking()
            .OrderBy(video => video.WatchState)
            .ThenByDescending(video => video.PublishedUtc)
            .ThenBy(video => video.Title)
            .ToListAsync();
    }

    public async Task<List<VideoEntry>> GetNeedToWatchVideosAsync(int take = 6)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Videos
            .AsNoTracking()
            .Where(video => video.WatchState == VideoWatchState.NeedToWatch)
            .OrderByDescending(video => video.PublishedUtc)
            .Take(take)
            .ToListAsync();
    }

    public async Task<List<TrainingItem>> GetOverdueItemsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var today = DateTime.Today;

        return await db.TrainingItems
            .AsNoTracking()
            .Where(item => item.Status != TrackerStatus.Completed && item.TargetDate < today)
            .OrderBy(item => item.TargetDate)
            .ThenByDescending(item => item.Priority)
            .ToListAsync();
    }

    public async Task<Dictionary<string, AnnouncementState>> GetAnnouncementStateLookupAsync(IEnumerable<AnnouncementItem> announcements)
    {
        var urls = announcements
            .Select(announcement => announcement.Url)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (urls.Count == 0)
        {
            return new Dictionary<string, AnnouncementState>(StringComparer.OrdinalIgnoreCase);
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var states = await db.AnnouncementStates
            .AsNoTracking()
            .Where(state => urls.Contains(state.Url))
            .ToListAsync();

        return states.ToDictionary(state => state.Url, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<AnnouncementState> MarkAnnouncementOpenedAsync(AnnouncementItem announcement)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var state = await UpsertAnnouncementStateAsync(db, announcement);
        var now = DateTime.UtcNow;

        state.IsSeen = true;
        state.FirstSeenUtc ??= now;
        state.LastSeenUtc = now;
        state.LastOpenedUtc = now;
        state.UpdatedUtc = now;

        await db.SaveChangesAsync();
        return state;
    }

    public async Task<AnnouncementState> SetAnnouncementSeenAsync(AnnouncementItem announcement, bool isSeen)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var state = await UpsertAnnouncementStateAsync(db, announcement);
        var now = DateTime.UtcNow;

        state.IsSeen = isSeen;
        if (isSeen)
        {
            state.FirstSeenUtc ??= now;
            state.LastSeenUtc = now;
        }

        state.UpdatedUtc = now;
        await db.SaveChangesAsync();
        return state;
    }

    public async Task<ResourceEntry> SaveAnnouncementAsResourceAsync(AnnouncementItem announcement)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var existingResource = await db.Resources.FirstOrDefaultAsync(resource => resource.Url == announcement.Url);

        if (existingResource is null)
        {
            var nextSortOrder = await db.Resources
                .Where(resource => resource.Section == announcement.Topic)
                .Select(resource => resource.SortOrder)
                .DefaultIfEmpty(0)
                .MaxAsync() + 10;

            existingResource = BuildAnnouncementResource(announcement, nextSortOrder, now);
            db.Resources.Add(existingResource);
        }

        var state = await UpsertAnnouncementStateAsync(db, announcement);
        state.IsSeen = true;
        state.IsSavedToResources = true;
        state.FirstSeenUtc ??= now;
        state.LastSeenUtc = now;
        state.SavedToResourcesUtc = now;
        state.UpdatedUtc = now;

        await db.SaveChangesAsync();
        return existingResource;
    }

    public async Task SaveTrainingItemAsync(TrainingItem item)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;

        if (item.Id == 0)
        {
            item.CreatedUtc = now;
            item.UpdatedUtc = now;
            item.LastStatusChangedUtc = now;
            item.CompletedUtc = item.Status == TrackerStatus.Completed ? now : null;
            db.TrainingItems.Add(item);
        }
        else
        {
            var existing = await db.TrainingItems.FirstAsync(existingItem => existingItem.Id == item.Id);
            var statusChanged = existing.Status != item.Status;
            existing.Title = item.Title;
            existing.Domain = item.Domain;
            existing.Category = item.Category;
            existing.Description = item.Description;
            existing.TargetDate = item.TargetDate;
            existing.Status = item.Status;
            existing.Lane = item.Lane;
            existing.Type = item.Type;
            existing.ProgressPercent = item.ProgressPercent;
            existing.EstimatedHours = item.EstimatedHours;
            existing.Priority = item.Priority;
            existing.ProjectDriven = item.ProjectDriven;
            existing.Notes = item.Notes;
            existing.Evidence = item.Evidence;
            existing.UpdatedUtc = now;

            if (statusChanged)
            {
                existing.LastStatusChangedUtc = now;
            }

            if (item.Status == TrackerStatus.Completed)
            {
                existing.CompletedUtc ??= now;
            }
            else if (statusChanged)
            {
                existing.CompletedUtc = null;
            }
        }

        await db.SaveChangesAsync();
    }

    public async Task DeleteTrainingItemAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.TrainingItems.FirstOrDefaultAsync(existing => existing.Id == id);
        if (item is null)
        {
            return;
        }

        db.TrainingItems.Remove(item);
        await db.SaveChangesAsync();
    }

    public async Task SaveResourceAsync(ResourceEntry resource)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;

        if (resource.Id == 0)
        {
            resource.CreatedUtc = now;
            resource.UpdatedUtc = now;
            db.Resources.Add(resource);
        }
        else
        {
            var existing = await db.Resources.FirstAsync(existingResource => existingResource.Id == resource.Id);
            existing.Title = resource.Title;
            existing.Section = resource.Section;
            existing.Url = resource.Url;
            existing.Kind = resource.Kind;
            existing.IsPinned = resource.IsPinned;
            existing.Summary = resource.Summary;
            existing.Tags = resource.Tags;
            existing.Notes = resource.Notes;
            existing.SortOrder = resource.SortOrder;
            existing.LastOpenedUtc = resource.LastOpenedUtc;
            existing.UpdatedUtc = now;
        }

        await db.SaveChangesAsync();
    }

    public async Task TouchResourceOpenedAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var resource = await db.Resources.FirstOrDefaultAsync(existing => existing.Id == id);
        if (resource is null)
        {
            return;
        }

        resource.LastOpenedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task DeleteResourceAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var resource = await db.Resources.FirstOrDefaultAsync(existing => existing.Id == id);
        if (resource is null)
        {
            return;
        }

        db.Resources.Remove(resource);
        await db.SaveChangesAsync();
    }

    public async Task SaveVideoChannelAsync(VideoChannel channel)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var normalizedHandle = NormalizeHandle(channel.Handle);
        var normalizedChannelUrl = string.IsNullOrWhiteSpace(channel.ChannelUrl)
            ? string.Empty
            : channel.ChannelUrl.Trim();

        if (channel.Id == 0)
        {
            var duplicate = await db.VideoChannels.FirstOrDefaultAsync(existing =>
                existing.Handle == normalizedHandle ||
                existing.ChannelId == channel.ChannelId);

            if (duplicate is not null)
            {
                duplicate.DisplayName = channel.DisplayName;
                duplicate.ChannelId = channel.ChannelId;
                duplicate.ChannelUrl = normalizedChannelUrl;
                duplicate.Description = channel.Description;
                duplicate.ThumbnailUrl = channel.ThumbnailUrl;
                duplicate.IsSeeded = duplicate.IsSeeded || channel.IsSeeded;
                duplicate.UpdatedUtc = now;
                duplicate.LastSyncedUtc = channel.LastSyncedUtc ?? duplicate.LastSyncedUtc;
            }
            else
            {
                channel.Handle = normalizedHandle;
                channel.ChannelUrl = normalizedChannelUrl;
                channel.CreatedUtc = now;
                channel.UpdatedUtc = now;
                db.VideoChannels.Add(channel);
            }
        }
        else
        {
            var existing = await db.VideoChannels.FirstAsync(current => current.Id == channel.Id);
            existing.DisplayName = channel.DisplayName;
            existing.Handle = normalizedHandle;
            existing.ChannelId = channel.ChannelId;
            existing.ChannelUrl = normalizedChannelUrl;
            existing.Description = channel.Description;
            existing.ThumbnailUrl = channel.ThumbnailUrl;
            existing.IsSeeded = channel.IsSeeded;
            existing.UpdatedUtc = now;
            existing.LastSyncedUtc = channel.LastSyncedUtc;
        }

        await db.SaveChangesAsync();
    }

    public async Task DeleteVideoChannelAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var channel = await db.VideoChannels.FirstOrDefaultAsync(existing => existing.Id == id);
        if (channel is null)
        {
            return;
        }

        db.VideoChannels.Remove(channel);
        await db.SaveChangesAsync();
    }

    public async Task UpdateVideoWatchStateAsync(int id, VideoWatchState watchState)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var video = await db.Videos.FirstOrDefaultAsync(existing => existing.Id == id);
        if (video is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        video.WatchState = watchState;
        video.UpdatedUtc = now;
        video.LastViewedUtc = watchState == VideoWatchState.Seen ? now : video.LastViewedUtc;
        video.RemovedUtc = watchState == VideoWatchState.Removed ? now : null;
        await db.SaveChangesAsync();
    }

    public async Task MarkVideoOpenedAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var video = await db.Videos.FirstOrDefaultAsync(existing => existing.Id == id);
        if (video is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        video.WatchState = VideoWatchState.Seen;
        video.LastViewedUtc = now;
        video.RemovedUtc = null;
        video.UpdatedUtc = now;
        await db.SaveChangesAsync();
    }

    public async Task UpsertVideosAsync(IEnumerable<VideoEntry> videos)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;

        foreach (var video in videos)
        {
            var existing = await db.Videos.FirstOrDefaultAsync(current => current.YouTubeVideoId == video.YouTubeVideoId);
            if (existing is null)
            {
                video.CreatedUtc = now;
                video.UpdatedUtc = now;
                db.Videos.Add(video);
                continue;
            }

            existing.ChannelId = video.ChannelId;
            existing.Title = video.Title;
            existing.Url = video.Url;
            existing.ThumbnailUrl = video.ThumbnailUrl;
            existing.Summary = video.Summary;
            existing.ChannelTitle = video.ChannelTitle;
            existing.PublishedUtc = video.PublishedUtc;
            existing.LastSyncedUtc = video.LastSyncedUtc;
            existing.UpdatedUtc = now;
        }

        await db.SaveChangesAsync();
    }

    public async Task SaveNoteAsync(NoteEntry note)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;

        if (note.Id == 0)
        {
            note.CreatedUtc = now;
            note.UpdatedUtc = now;
            db.Notes.Add(note);
        }
        else
        {
            var existing = await db.Notes.FirstAsync(existingNote => existingNote.Id == note.Id);
            existing.Title = note.Title;
            existing.Category = note.Category;
            existing.RelatedArea = note.RelatedArea;
            existing.Tags = note.Tags;
            existing.Content = note.Content;
            existing.IsPinned = note.IsPinned;
            existing.UpdatedUtc = now;
        }

        await db.SaveChangesAsync();
    }

    public async Task DeleteNoteAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var note = await db.Notes.FirstOrDefaultAsync(existing => existing.Id == id);
        if (note is null)
        {
            return;
        }

        db.Notes.Remove(note);
        await db.SaveChangesAsync();
    }

    private static string NormalizeHandle(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return string.Empty;
        }

        var trimmed = handle.Trim();
        return trimmed.StartsWith('@') ? trimmed : $"@{trimmed}";
    }

    private static ResourceEntry BuildAnnouncementResource(AnnouncementItem announcement, int sortOrder, DateTime now)
    {
        var tags = string.Join(", ", new[] { "announcement", announcement.Topic, announcement.Source }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var notes = $"Saved from the {GetAnnouncementStreamLabel(announcement.Stream).ToLowerInvariant()} source {announcement.Source} on {now:yyyy-MM-dd}.";

        return new ResourceEntry
        {
            Title = TruncateValue(announcement.Title, 140),
            Section = TruncateValue(announcement.Topic, 80),
            Url = TruncateValue(announcement.Url, 500),
            Kind = ResourceKind.Documentation,
            Summary = TruncateValue(announcement.Summary, 1500),
            Tags = TruncateValue(tags, 300),
            Notes = TruncateValue(notes, 2000),
            SortOrder = sortOrder,
            CreatedUtc = now,
            UpdatedUtc = now
        };
    }

    private static string TruncateValue(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string GetAnnouncementStreamLabel(AnnouncementStream stream) => stream switch
    {
        AnnouncementStream.MicrosoftOfficial => "Microsoft updates",
        AnnouncementStream.IndustryInsights => "Thought leaders and industry",
        _ => "Announcements"
    };

    private static void SyncAnnouncementState(AnnouncementState state, AnnouncementItem announcement)
    {
        state.Stream = announcement.Stream;
        state.Title = TruncateValue(announcement.Title, 200);
        state.Source = TruncateValue(announcement.Source, 120);
        state.Topic = TruncateValue(announcement.Topic, 120);
        state.Summary = TruncateValue(announcement.Summary, 2000);
        state.PublishedUtc = announcement.PublishedUtc;
    }

    private static async Task<AnnouncementState> UpsertAnnouncementStateAsync(TrackerDbContext db, AnnouncementItem announcement)
    {
        var state = await db.AnnouncementStates.FirstOrDefaultAsync(existing => existing.Url == announcement.Url);
        if (state is null)
        {
            state = new AnnouncementState
            {
                Url = announcement.Url
            };
            SyncAnnouncementState(state, announcement);
            db.AnnouncementStates.Add(state);
            return state;
        }

        SyncAnnouncementState(state, announcement);
        return state;
    }
}
