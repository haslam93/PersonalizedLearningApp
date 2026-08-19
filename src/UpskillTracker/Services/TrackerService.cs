using Microsoft.EntityFrameworkCore;
using Npgsql;
using UpskillTracker.Data;
using UpskillTracker.Models;

namespace UpskillTracker.Services;

public class TrackerService(
    IDbContextFactory<TrackerDbContext> dbFactory,
    ILogger<TrackerService> logger,
    DatabaseAvailabilityState databaseAvailabilityState)
{
    public bool HasRecentTransientReadFailure => databaseAvailabilityState.IsUnavailable;

    public string DatabaseUnavailableMessage => databaseAvailabilityState.UserMessage;

    public DateTimeOffset? DatabaseUnavailableSinceUtc => databaseAvailabilityState.LastFailureUtc;

    public async Task<DashboardSnapshot> GetDashboardSnapshotAsync()
    {
        return await ExecuteReadAsync(async db =>
        {
            var today = GetUtcToday();
            var endOfMonth = new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month), 0, 0, 0, DateTimeKind.Utc);

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
            var activeItems = trainingItems
                .Where(item => item.Status != TrackerStatus.Completed)
                .ToList();
            var committedItems = trainingItems
                .Where(item => !TrainingPlanPrioritizer.IsNiceToHave(item))
                .ToList();
            var activeCommittedItems = activeItems
                .Where(item => !TrainingPlanPrioritizer.IsNiceToHave(item))
                .ToList();
            var completedCommittedItems = committedItems.Count(item => item.Status == TrackerStatus.Completed);
            var upcomingItems = activeItems
                .Where(item => item.TargetDate >= today)
                .OrderBy(item => item.TargetDate)
                .ThenByDescending(item => item.Priority)
                .Take(6)
                .ToList();
            var focusItems = TrainingPlanPrioritizer
                .OrderForFocus(activeItems, today)
                .Take(5)
                .ToList();

            return new DashboardSnapshot
            {
                TotalItems = trainingItems.Count,
                CompletedItems = completedItems,
                InProgressItems = trainingItems.Count(item => item.Status == TrackerStatus.InProgress),
                OverdueItems = activeCommittedItems.Count(item => item.TargetDate < today),
                DueThisMonth = activeCommittedItems.Count(item => item.TargetDate >= today && item.TargetDate <= endOfMonth),
                DueNext14Days = activeCommittedItems.Count(item => item.TargetDate >= today && item.TargetDate <= today.AddDays(14)),
                AtRiskItems = activeItems.Count(item => TrainingPlanPrioritizer.IsAtRisk(item, today)),
                CoreRemainingItems = activeItems.Count(item => !TrainingPlanPrioritizer.IsNiceToHave(item)),
                NiceToHaveItems = activeItems.Count(TrainingPlanPrioritizer.IsNiceToHave),
                CertificationGoals = activeItems.Count(item => item.Type == TrainingItemType.Certification),
                RapidRampItems = trainingItems.Count(item => item.Lane == LearningLane.RapidRamp && item.Status != TrackerStatus.Completed),
                CompletionRate = trainingItems.Count == 0 ? 0 : Math.Round((decimal)completedItems / trainingItems.Count * 100, 1),
                CommitmentCompletionRate = committedItems.Count == 0 ? 0 : Math.Round((decimal)completedCommittedItems / committedItems.Count * 100, 1),
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
        }, new DashboardSnapshot(), "dashboard snapshot");
    }

    public async Task<List<TrainingItem>> GetTrainingItemsAsync()
    {
        return await ExecuteReadAsync(db => db.TrainingItems
            .AsNoTracking()
            .OrderBy(item => item.TargetDate)
            .ThenByDescending(item => item.Priority)
            .ToListAsync(), new List<TrainingItem>(), "training items");
    }

    public async Task<LearningHistorySnapshot> GetLearningHistoryAsync()
    {
        return await ExecuteReadAsync(async db =>
        {
            var activities = await db.LearningActivities
                .AsNoTracking()
                .OrderByDescending(activity => activity.OccurredUtc)
                .ToListAsync();

            var today = GetUtcToday();
            var currentMonthStart = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var explorationTypes = new[]
            {
                LearningActivityType.ResourceRead,
                LearningActivityType.VideoWatched,
                LearningActivityType.AnnouncementRead,
                LearningActivityType.ToolUsed
            };
            var completionTypes = new[]
            {
                LearningActivityType.ItemCompleted,
                LearningActivityType.CertificationEarned
            };
            var bestMonth = activities
                .GroupBy(activity => new DateTime(activity.OccurredUtc.Year, activity.OccurredUtc.Month, 1))
                .OrderByDescending(group => group.Count())
                .ThenByDescending(group => group.Key)
                .FirstOrDefault();

            return new LearningHistorySnapshot
            {
                TotalActivities = activities.Count,
                ActiveDays = activities.Select(activity => activity.OccurredUtc.Date).Distinct().Count(),
                ActiveDaysLast30 = activities
                    .Where(activity => activity.OccurredUtc >= today.AddDays(-29))
                    .Select(activity => activity.OccurredUtc.Date)
                    .Distinct()
                    .Count(),
                CompletedMilestones = activities.Count(activity => completionTypes.Contains(activity.Type)),
                ThingsExplored = activities.Count(activity => explorationTypes.Contains(activity.Type)),
                Reflections = activities.Count(activity => activity.Type == LearningActivityType.ReflectionAdded),
                CompletedHours = activities
                    .Where(activity => completionTypes.Contains(activity.Type))
                    .Sum(activity => activity.EstimatedHours ?? 0),
                CurrentMonthActivities = activities.Count(activity => activity.OccurredUtc >= currentMonthStart),
                BestMonthLabel = bestMonth?.Key.ToString("MMMM yyyy") ?? "No activity yet",
                BestMonthActivities = bestMonth?.Count() ?? 0,
                FirstActivityUtc = activities.LastOrDefault()?.OccurredUtc,
                LastActivityUtc = activities.FirstOrDefault()?.OccurredUtc,
                Activities = activities,
                RecentActivities = activities.Take(8).ToList()
            };
        }, new LearningHistorySnapshot(), "learning history");
    }

    public async Task<bool> ImportCertificationAsync(CertificationCatalogItem certification)
    {
        return await ExecuteWriteAsync(async db =>
        {
            var alreadyImported = await db.TrainingItems.AnyAsync(item =>
                item.Type == TrainingItemType.Certification &&
                (item.Title == certification.Title || item.Category == certification.Code));

            if (alreadyImported)
            {
                return false;
            }

            db.TrainingItems.Add(certification.CreateTrainingItem());

            var resourceExists = await db.Resources.AnyAsync(resource => resource.Url == certification.CredentialUrl);
            if (!resourceExists)
            {
                db.Resources.Add(certification.CreateResource());
            }

            await db.SaveChangesAsync();
            return true;
        }, "import certification");
    }

    public async Task<List<ResourceEntry>> GetResourcesAsync()
    {
        return await ExecuteReadAsync(db => db.Resources
            .AsNoTracking()
            .OrderBy(resource => resource.Section)
            .ThenByDescending(resource => resource.IsPinned)
            .ThenBy(resource => resource.SortOrder)
            .ThenBy(resource => resource.Title)
            .ToListAsync(), new List<ResourceEntry>(), "resources");
    }

    public async Task<List<NoteEntry>> GetNotesAsync()
    {
        return await ExecuteReadAsync(db => db.Notes
            .AsNoTracking()
            .OrderByDescending(note => note.IsPinned)
            .ThenByDescending(note => note.UpdatedUtc)
            .ToListAsync(), new List<NoteEntry>(), "notes");
    }

    public async Task<List<VideoChannel>> GetVideoChannelsAsync()
    {
        return await ExecuteReadAsync(db => db.VideoChannels
            .AsNoTracking()
            .OrderBy(channel => channel.DisplayName)
            .ToListAsync(), new List<VideoChannel>(), "video channels");
    }

    public async Task<List<VideoEntry>> GetVideosAsync()
    {
        return await ExecuteReadAsync(db => db.Videos
            .AsNoTracking()
            .OrderBy(video => video.WatchState)
            .ThenByDescending(video => video.PublishedUtc)
            .ThenBy(video => video.Title)
            .ToListAsync(), new List<VideoEntry>(), "videos");
    }

    public async Task<List<VideoEntry>> GetNeedToWatchVideosAsync(int take = 6)
    {
        return await ExecuteReadAsync(db => db.Videos
            .AsNoTracking()
            .Where(video => video.WatchState == VideoWatchState.NeedToWatch)
            .OrderByDescending(video => video.PublishedUtc)
            .Take(take)
            .ToListAsync(), new List<VideoEntry>(), "queued videos");
    }

    public async Task<List<TrainingItem>> GetOverdueItemsAsync()
    {
        var today = GetUtcToday();

        return await ExecuteReadAsync(db => db.TrainingItems
            .AsNoTracking()
            .Where(item => item.Status != TrackerStatus.Completed && item.TargetDate < today)
            .OrderBy(item => item.TargetDate)
            .ThenByDescending(item => item.Priority)
            .ToListAsync(), new List<TrainingItem>(), "overdue items");
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

        return await ExecuteReadAsync(async db =>
        {
            var states = await db.AnnouncementStates
                .AsNoTracking()
                .Where(state => urls.Contains(state.Url))
                .ToListAsync();

            return states.ToDictionary(state => state.Url, StringComparer.OrdinalIgnoreCase);
        }, new Dictionary<string, AnnouncementState>(StringComparer.OrdinalIgnoreCase), "announcement state lookup");
    }

    public async Task<AnnouncementState> MarkAnnouncementOpenedAsync(AnnouncementItem announcement)
    {
        return await ExecuteWriteAsync(async db =>
        {
            var state = await UpsertAnnouncementStateAsync(db, announcement);
            var now = DateTime.UtcNow;

            state.IsSeen = true;
            state.FirstSeenUtc ??= now;
            state.LastSeenUtc = now;
            state.LastOpenedUtc = now;
            state.UpdatedUtc = now;

            await db.SaveChangesAsync();
            await LearningActivityRecorder.TryRecordAsync(db, new LearningActivity
            {
                Type = LearningActivityType.AnnouncementRead,
                Title = announcement.Title,
                Area = announcement.Topic,
                Detail = announcement.Summary,
                SourceKind = "Announcement",
                OccurredUtc = now,
                DeduplicationKey = LearningActivityRecorder.BuildDailyKey(
                    "announcement",
                    LearningActivityRecorder.BuildStableSourceKey(announcement.Url),
                    now)
            }, logger);
            return state;
        }, "mark announcement opened");
    }

    public async Task<AnnouncementState> SetAnnouncementSeenAsync(AnnouncementItem announcement, bool isSeen)
    {
        return await ExecuteWriteAsync(async db =>
        {
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
        }, "set announcement seen state");
    }

    public async Task<ResourceEntry> SaveAnnouncementAsResourceAsync(AnnouncementItem announcement)
    {
        return await ExecuteWriteAsync(async db =>
        {
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
        }, "save announcement as resource");
    }

    public async Task SaveTrainingItemAsync(TrainingItem item)
    {
        await ExecuteWriteAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var normalizedTargetDate = NormalizeUtcDate(item.TargetDate);
            LearningActivity? activity = null;

            if (item.Id == 0)
            {
                item.TargetDate = normalizedTargetDate;
                item.ProgressPercent = item.Status == TrackerStatus.Completed ? 100 : item.ProgressPercent;
                item.CreatedUtc = now;
                item.UpdatedUtc = now;
                item.LastStatusChangedUtc = now;
                item.CompletedUtc = item.Status == TrackerStatus.Completed ? now : null;
                db.TrainingItems.Add(item);
                await db.SaveChangesAsync();

                activity = item.Status switch
                {
                    TrackerStatus.Completed => BuildTrainingActivity(
                        item,
                        item.Type == TrainingItemType.Certification
                            ? LearningActivityType.CertificationEarned
                            : LearningActivityType.ItemCompleted,
                        now),
                    TrackerStatus.InProgress => BuildTrainingActivity(item, LearningActivityType.ItemStarted, now),
                    _ => null
                };
            }
            else
            {
                var existing = await db.TrainingItems.FirstAsync(existingItem => existingItem.Id == item.Id);
                var statusChanged = existing.Status != item.Status;
                var previousStatus = existing.Status;
                var previousProgress = existing.ProgressPercent;
                var normalizedProgress = item.Status == TrackerStatus.Completed ? 100 : item.ProgressPercent;
                existing.Title = item.Title;
                existing.Domain = item.Domain;
                existing.Category = item.Category;
                existing.Description = item.Description;
                existing.TargetDate = normalizedTargetDate;
                existing.Status = item.Status;
                existing.Lane = item.Lane;
                existing.Type = item.Type;
                existing.ProgressPercent = normalizedProgress;
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

                await db.SaveChangesAsync();

                if (item.Status == TrackerStatus.Completed && previousStatus != TrackerStatus.Completed)
                {
                    activity = BuildTrainingActivity(
                        existing,
                        existing.Type == TrainingItemType.Certification
                            ? LearningActivityType.CertificationEarned
                            : LearningActivityType.ItemCompleted,
                        now);
                }
                else if (item.Status == TrackerStatus.InProgress && previousStatus != TrackerStatus.InProgress)
                {
                    activity = BuildTrainingActivity(existing, LearningActivityType.ItemStarted, now);
                }
                else if (normalizedProgress > previousProgress)
                {
                    activity = BuildTrainingActivity(existing, LearningActivityType.ProgressUpdated, now);
                }
            }

            if (activity is not null)
            {
                await LearningActivityRecorder.TryRecordAsync(db, activity, logger);
            }
        }, "save training item");
    }

    public async Task DeleteTrainingItemAsync(int id)
    {
        await ExecuteWriteAsync(async db =>
        {
            var item = await db.TrainingItems.FirstOrDefaultAsync(existing => existing.Id == id);
            if (item is null)
            {
                return;
            }

            db.TrainingItems.Remove(item);
            await db.SaveChangesAsync();
        }, "delete training item");
    }

    public async Task SaveResourceAsync(ResourceEntry resource)
    {
        await ExecuteWriteAsync(async db =>
        {
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
        }, "save resource");
    }

    public async Task TouchResourceOpenedAsync(int id)
    {
        await ExecuteWriteAsync(async db =>
        {
            var resource = await db.Resources.FirstOrDefaultAsync(existing => existing.Id == id);
            if (resource is null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            resource.LastOpenedUtc = now;
            await db.SaveChangesAsync();
            await LearningActivityRecorder.TryRecordAsync(db, new LearningActivity
            {
                Type = LearningActivityType.ResourceRead,
                Title = resource.Title,
                Area = resource.Section,
                Detail = resource.Summary,
                SourceKind = "Resource",
                SourceId = resource.Id,
                OccurredUtc = now,
                DeduplicationKey = LearningActivityRecorder.BuildDailyKey("resource", resource.Id.ToString(), now)
            }, logger);
        }, "touch resource opened");
    }

    public async Task DeleteResourceAsync(int id)
    {
        await ExecuteWriteAsync(async db =>
        {
            var resource = await db.Resources.FirstOrDefaultAsync(existing => existing.Id == id);
            if (resource is null)
            {
                return;
            }

            db.Resources.Remove(resource);
            await db.SaveChangesAsync();
        }, "delete resource");
    }

    public async Task SaveVideoChannelAsync(VideoChannel channel)
    {
        await ExecuteWriteAsync(async db =>
        {
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
        }, "save video channel");
    }

    public async Task DeleteVideoChannelAsync(int id)
    {
        await ExecuteWriteAsync(async db =>
        {
            var channel = await db.VideoChannels.FirstOrDefaultAsync(existing => existing.Id == id);
            if (channel is null)
            {
                return;
            }

            db.VideoChannels.Remove(channel);
            await db.SaveChangesAsync();
        }, "delete video channel");
    }

    public async Task UpdateVideoWatchStateAsync(int id, VideoWatchState watchState)
    {
        await ExecuteWriteAsync(async db =>
        {
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

            if (watchState == VideoWatchState.Seen)
            {
                await RecordVideoActivityAsync(db, video, now);
            }
        }, "update video watch state");
    }

    public async Task MarkVideoOpenedAsync(int id)
    {
        await ExecuteWriteAsync(async db =>
        {
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
            await RecordVideoActivityAsync(db, video, now);
        }, "mark video opened");
    }

    public async Task UpsertVideosAsync(IEnumerable<VideoEntry> videos)
    {
        await ExecuteWriteAsync(async db =>
        {
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
        }, "upsert videos");
    }

    public async Task SaveNoteAsync(NoteEntry note)
    {
        await ExecuteWriteAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var contentChanged = true;

            if (note.Id == 0)
            {
                note.CreatedUtc = now;
                note.UpdatedUtc = now;
                db.Notes.Add(note);
            }
            else
            {
                var existing = await db.Notes.FirstAsync(existingNote => existingNote.Id == note.Id);
                contentChanged = !existing.Content.Equals(note.Content, StringComparison.Ordinal);
                existing.Title = note.Title;
                existing.Category = note.Category;
                existing.RelatedArea = note.RelatedArea;
                existing.Tags = note.Tags;
                existing.Content = note.Content;
                existing.IsPinned = note.IsPinned;
                existing.UpdatedUtc = now;
            }

            await db.SaveChangesAsync();

            if (contentChanged)
            {
                await LearningActivityRecorder.TryRecordAsync(db, new LearningActivity
                {
                    Type = LearningActivityType.ReflectionAdded,
                    Title = note.Title,
                    Area = string.IsNullOrWhiteSpace(note.RelatedArea) ? note.Category : note.RelatedArea,
                    Detail = note.Content,
                    SourceKind = "Note",
                    SourceId = note.Id,
                    OccurredUtc = now,
                    DeduplicationKey = LearningActivityRecorder.BuildReflectionKey(note.Id, note.Content, now)
                }, logger);
            }
        }, "save note");
    }

    public async Task<bool> RecordToolOpenedAsync(LearningTool tool)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var now = DateTime.UtcNow;
            var recorded = await LearningActivityRecorder.TryRecordAsync(db, new LearningActivity
            {
                Type = LearningActivityType.ToolUsed,
                Title = tool.Title,
                Area = tool.Category,
                Detail = tool.BestFor,
                SourceKind = "Tool",
                OccurredUtc = now,
                DeduplicationKey = LearningActivityRecorder.BuildDailyKey("tool", tool.Key, now)
            }, logger);

            if (recorded)
            {
                databaseAvailabilityState.MarkAvailable();
            }
            else
            {
                databaseAvailabilityState.MarkUnavailable();
            }

            return recorded;
        }
        catch (Exception ex) when (IsTransientReadException(ex))
        {
            databaseAvailabilityState.MarkUnavailable();
            logger.LogWarning(ex, "Tool launch history could not be recorded because the database is unavailable.");
            return false;
        }
    }

    public async Task DeleteNoteAsync(int id)
    {
        await ExecuteWriteAsync(async db =>
        {
            var note = await db.Notes.FirstOrDefaultAsync(existing => existing.Id == id);
            if (note is null)
            {
                return;
            }

            db.Notes.Remove(note);
            await db.SaveChangesAsync();
        }, "delete note");
    }

    private static LearningActivity BuildTrainingActivity(TrainingItem item, LearningActivityType type, DateTime occurredUtc)
    {
        var detail = type switch
        {
            LearningActivityType.ItemStarted => $"Started {TrainingPlanPrioritizer.GetCommitmentLabel(item).ToLowerInvariant()} work in {item.Domain}.",
            LearningActivityType.ProgressUpdated => $"Moved preparation forward to {item.ProgressPercent}% complete.",
            LearningActivityType.CertificationEarned => string.IsNullOrWhiteSpace(item.Evidence)
                ? $"Earned or completed the {item.Title} certification goal."
                : item.Evidence,
            _ => string.IsNullOrWhiteSpace(item.Evidence)
                ? $"Completed {item.Type.ToString().ToLowerInvariant()} work in {item.Domain}."
                : item.Evidence
        };

        return new LearningActivity
        {
            Type = type,
            Title = item.Title,
            Area = item.Domain,
            Detail = detail,
            SourceKind = "TrainingItem",
            SourceId = item.Id,
            ProgressPercent = item.ProgressPercent,
            EstimatedHours = item.EstimatedHours,
            OccurredUtc = occurredUtc,
            DeduplicationKey = LearningActivityRecorder.BuildTrainingKey(item.Id, type, occurredUtc, item.ProgressPercent)
        };
    }

    private async Task RecordVideoActivityAsync(TrackerDbContext db, VideoEntry video, DateTime occurredUtc)
    {
        await LearningActivityRecorder.TryRecordAsync(db, new LearningActivity
        {
            Type = LearningActivityType.VideoWatched,
            Title = video.Title,
            Area = video.ChannelTitle,
            Detail = video.Summary,
            SourceKind = "Video",
            SourceId = video.Id,
            OccurredUtc = occurredUtc,
            DeduplicationKey = LearningActivityRecorder.BuildDailyKey("video", video.Id.ToString(), occurredUtc)
        }, logger);
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

    private static DateTime GetUtcToday()
        => DateTime.UtcNow.Date;

    private static DateTime NormalizeUtcDate(DateTime value)
    {
        var calendarDate = value.Kind switch
        {
            DateTimeKind.Utc => value.Date,
            DateTimeKind.Local => value.ToUniversalTime().Date,
            _ => value.Date
        };

        return DateTime.SpecifyKind(calendarDate, DateTimeKind.Utc);
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

    private async Task<TResult> ExecuteReadAsync<TResult>(Func<TrackerDbContext, Task<TResult>> operation, TResult fallback, string operationName)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var result = await operation(db);
            databaseAvailabilityState.MarkAvailable();
            return result;
        }
        catch (Exception ex) when (IsTransientReadException(ex))
        {
            databaseAvailabilityState.MarkUnavailable();
            logger.LogWarning(ex, "Tracker read {OperationName} failed because the database is temporarily unavailable. Returning fallback data.", operationName);
            return fallback;
        }
    }

    private async Task ExecuteWriteAsync(Func<TrackerDbContext, Task> operation, string operationName)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await operation(db);
            databaseAvailabilityState.MarkAvailable();
        }
        catch (Exception ex) when (IsTransientReadException(ex))
        {
            databaseAvailabilityState.MarkUnavailable();
            logger.LogWarning(ex, "Tracker write {OperationName} failed because the database is unavailable.", operationName);
            throw new TrackerStorageUnavailableException(databaseAvailabilityState.UserMessage, ex);
        }
    }

    private async Task<TResult> ExecuteWriteAsync<TResult>(Func<TrackerDbContext, Task<TResult>> operation, string operationName)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var result = await operation(db);
            databaseAvailabilityState.MarkAvailable();
            return result;
        }
        catch (Exception ex) when (IsTransientReadException(ex))
        {
            databaseAvailabilityState.MarkUnavailable();
            logger.LogWarning(ex, "Tracker write {OperationName} failed because the database is unavailable.", operationName);
            throw new TrackerStorageUnavailableException(databaseAvailabilityState.UserMessage, ex);
        }
    }

    private static bool IsTransientReadException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException or TimeoutException)
            {
                return true;
            }
        }

        return false;
    }
}
