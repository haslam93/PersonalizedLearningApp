using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using UpskillTracker.Models;
using UpskillTracker.Services;

namespace UpskillTracker.Data;

public static class DatabaseInitializer
{
    private const string LegacySqliteImportMetadataKey = "legacy-sqlite-import-v1";
    private const string LegacySqliteLearningHistoryImportMetadataKey = "legacy-sqlite-learning-history-import-v1";
    private const string PlanExpansionMetadataKey = "fabric-databricks-certifications-plan-v1";
    private const string LearningHistoryBackfillMetadataKey = "learning-history-backfill-v1";
    private static readonly HashSet<string> SeedNoteTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Operating model for this tracker",
        "Customer-facing architecture note template",
        "Hands-on lab reflection template"
    };

    public static async Task InitializeAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TrackerDbContext>>();
        var environment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInitializer");
        var databaseAvailabilityState = scope.ServiceProvider.GetRequiredService<DatabaseAvailabilityState>();
        var storageOptions = scope.ServiceProvider.GetRequiredService<IOptions<StorageOptions>>().Value;

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();

            await db.Database.EnsureCreatedAsync();
            await EnsureVideoSchemaAsync(db);
            await EnsureLearningActivitySchemaAsync(db);

            if (IsPostgresProvider(db) && storageOptions.EnableLegacySqliteImport)
            {
                var legacySqliteConnectionString = ResolveLegacySqliteConnectionString(storageOptions, environment);
                await ImportLegacySqliteIfNeededAsync(db, legacySqliteConnectionString, logger);
                await ImportLegacyLearningHistoryIfNeededAsync(db, legacySqliteConnectionString, logger);
            }

            if (!await db.TrainingItems.AnyAsync())
            {
                db.TrainingItems.AddRange(GetSeedTrainingItems());
            }

            await EnsurePlanExpansionTrainingItemsAsync(db);
            await ImportLearningRadarItemsAsync(db, environment, logger);

            await EnsureSeedResourcesAsync(db);
            await EnsureSeedVideoChannelsAsync(db);

            if (!await db.Notes.AnyAsync())
            {
                db.Notes.AddRange(GetSeedNotes());
            }

            await db.SaveChangesAsync();
            await BackfillLearningHistoryAsync(db, logger);

            if (IsPostgresProvider(db))
            {
                await ResetIdentitySequencesAsync(db);
            }

            databaseAvailabilityState.MarkAvailable();
        }
        catch (Exception ex) when (IsTransientDatabaseException(ex))
        {
            databaseAvailabilityState.MarkUnavailable();
            logger.LogWarning(ex, "Skipping database initialization because PostgreSQL is currently unavailable. The app will continue in degraded mode until the database is started again.");
        }
    }

    private static async Task EnsureSeedResourcesAsync(TrackerDbContext db)
    {
        var existingResourceKeys = await db.Resources
            .AsNoTracking()
            .Select(resource => new { resource.Title, resource.Section })
            .ToListAsync();

        var keySet = existingResourceKeys
            .Select(resource => BuildResourceKey(resource.Title, resource.Section))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var resource in GetSeedResources())
        {
            if (keySet.Contains(BuildResourceKey(resource.Title, resource.Section)))
            {
                continue;
            }

            db.Resources.Add(resource);
        }
    }

    private static async Task EnsurePlanExpansionTrainingItemsAsync(TrackerDbContext db)
    {
        var expansionAlreadyApplied = await db.AppMetadataEntries
            .AsNoTracking()
            .AnyAsync(entry => entry.Key == PlanExpansionMetadataKey);

        if (expansionAlreadyApplied ||
            db.AppMetadataEntries.Local.Any(entry => entry.Key == PlanExpansionMetadataKey))
        {
            return;
        }

        var existingTitles = (await db.TrainingItems
            .AsNoTracking()
            .Select(item => item.Title)
            .ToListAsync())
            .Concat(db.TrainingItems.Local.Select(item => item.Title))
            .Select(title => title.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in GetPlanExpansionTrainingItems())
        {
            if (!existingTitles.Add(item.Title.Trim()))
            {
                continue;
            }

            db.TrainingItems.Add(item);
        }

        db.AppMetadataEntries.Add(new AppMetadataEntry
        {
            Key = PlanExpansionMetadataKey,
            Value = $"Applied:{DateTime.UtcNow:O}",
            UpdatedUtc = DateTime.UtcNow
        });
    }

    private static async Task EnsureVideoSchemaAsync(TrackerDbContext db)
    {
        if (!db.Database.IsSqlite())
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS VideoChannels (
                Id INTEGER NOT NULL CONSTRAINT PK_VideoChannels PRIMARY KEY AUTOINCREMENT,
                DisplayName TEXT NOT NULL,
                Handle TEXT NOT NULL,
                ChannelId TEXT NOT NULL,
                ChannelUrl TEXT NOT NULL,
                Description TEXT NOT NULL,
                ThumbnailUrl TEXT NOT NULL,
                IsSeeded INTEGER NOT NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                LastSyncedUtc TEXT NULL
            );
            """);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS Videos (
                Id INTEGER NOT NULL CONSTRAINT PK_Videos PRIMARY KEY AUTOINCREMENT,
                ChannelId INTEGER NOT NULL,
                YouTubeVideoId TEXT NOT NULL,
                Title TEXT NOT NULL,
                Url TEXT NOT NULL,
                ThumbnailUrl TEXT NOT NULL,
                Summary TEXT NOT NULL,
                ChannelTitle TEXT NOT NULL,
                PublishedUtc TEXT NOT NULL,
                WatchState INTEGER NOT NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                LastViewedUtc TEXT NULL,
                LastSyncedUtc TEXT NULL,
                CONSTRAINT FK_Videos_VideoChannels_ChannelId FOREIGN KEY (ChannelId) REFERENCES VideoChannels (Id) ON DELETE CASCADE
            );
            """);

        await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_VideoChannels_ChannelId ON VideoChannels (ChannelId);");
        await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_VideoChannels_Handle ON VideoChannels (Handle);");
        await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_Videos_YouTubeVideoId ON Videos (YouTubeVideoId);");
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_Videos_WatchState_PublishedUtc ON Videos (WatchState, PublishedUtc);");
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_Videos_ChannelId_PublishedUtc ON Videos (ChannelId, PublishedUtc);");
    }

    private static async Task EnsureLearningActivitySchemaAsync(TrackerDbContext db)
    {
        if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS LearningActivities (
                    Id INTEGER NOT NULL CONSTRAINT PK_LearningActivities PRIMARY KEY AUTOINCREMENT,
                    Type INTEGER NOT NULL,
                    Title TEXT NOT NULL,
                    Area TEXT NOT NULL,
                    Detail TEXT NOT NULL,
                    SourceKind TEXT NOT NULL,
                    SourceId INTEGER NULL,
                    ProgressPercent INTEGER NULL,
                    EstimatedHours TEXT NULL,
                    OccurredUtc TEXT NOT NULL,
                    DeduplicationKey TEXT NOT NULL
                );
                """);

            await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_LearningActivities_DeduplicationKey ON LearningActivities (DeduplicationKey);");
            await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_LearningActivities_OccurredUtc ON LearningActivities (OccurredUtc);");
            await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_LearningActivities_Type_OccurredUtc ON LearningActivities (Type, OccurredUtc);");
            return;
        }

        if (!IsPostgresProvider(db))
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "LearningActivities" (
                "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
                "Type" integer NOT NULL,
                "Title" character varying(180) NOT NULL,
                "Area" character varying(100) NOT NULL,
                "Detail" character varying(1000) NOT NULL,
                "SourceKind" character varying(40) NOT NULL,
                "SourceId" integer NULL,
                "ProgressPercent" integer NULL,
                "EstimatedHours" numeric(5,1) NULL,
                "OccurredUtc" timestamp with time zone NOT NULL,
                "DeduplicationKey" character varying(180) NOT NULL,
                CONSTRAINT "PK_LearningActivities" PRIMARY KEY ("Id")
            );
            """);

        await db.Database.ExecuteSqlRawAsync("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_LearningActivities_DeduplicationKey" ON "LearningActivities" ("DeduplicationKey");""");
        await db.Database.ExecuteSqlRawAsync("""CREATE INDEX IF NOT EXISTS "IX_LearningActivities_OccurredUtc" ON "LearningActivities" ("OccurredUtc");""");
        await db.Database.ExecuteSqlRawAsync("""CREATE INDEX IF NOT EXISTS "IX_LearningActivities_Type_OccurredUtc" ON "LearningActivities" ("Type", "OccurredUtc");""");
    }

    private static async Task BackfillLearningHistoryAsync(TrackerDbContext db, ILogger logger)
    {
        var alreadyBackfilled = await db.AppMetadataEntries
            .AsNoTracking()
            .AnyAsync(entry => entry.Key == LearningHistoryBackfillMetadataKey);

        if (alreadyBackfilled ||
            db.AppMetadataEntries.Local.Any(entry => entry.Key == LearningHistoryBackfillMetadataKey))
        {
            return;
        }

        var activities = new List<LearningActivity>();

        var trainingItems = await db.TrainingItems.AsNoTracking().ToListAsync();
        foreach (var item in trainingItems)
        {
            if (item.Status == TrackerStatus.Completed)
            {
                var occurredUtc = EnsureUtc(item.CompletedUtc ?? item.LastStatusChangedUtc ?? item.UpdatedUtc);
                var type = item.Type == TrainingItemType.Certification
                    ? LearningActivityType.CertificationEarned
                    : LearningActivityType.ItemCompleted;

                activities.Add(new LearningActivity
                {
                    Type = type,
                    Title = item.Title,
                    Area = item.Domain,
                    Detail = string.IsNullOrWhiteSpace(item.Evidence)
                        ? $"Completed {item.Type.ToString().ToLowerInvariant()} work in {item.Domain}."
                        : item.Evidence,
                    SourceKind = "TrainingItem",
                    SourceId = item.Id,
                    ProgressPercent = 100,
                    EstimatedHours = item.EstimatedHours,
                    OccurredUtc = occurredUtc,
                    DeduplicationKey = LearningActivityRecorder.BuildTrainingKey(item.Id, type, occurredUtc, 100)
                });
            }
            else if (item.Status == TrackerStatus.InProgress)
            {
                var occurredUtc = EnsureUtc(item.LastStatusChangedUtc ?? item.UpdatedUtc);
                activities.Add(new LearningActivity
                {
                    Type = LearningActivityType.ItemStarted,
                    Title = item.Title,
                    Area = item.Domain,
                    Detail = $"Started a {TrainingPlanPrioritizer.GetCommitmentLabel(item).ToLowerInvariant()} in {item.Domain}.",
                    SourceKind = "TrainingItem",
                    SourceId = item.Id,
                    ProgressPercent = item.ProgressPercent,
                    EstimatedHours = item.EstimatedHours,
                    OccurredUtc = occurredUtc,
                    DeduplicationKey = LearningActivityRecorder.BuildTrainingKey(item.Id, LearningActivityType.ItemStarted, occurredUtc, item.ProgressPercent)
                });
            }
        }

        var openedResources = await db.Resources
            .AsNoTracking()
            .Where(resource => resource.LastOpenedUtc.HasValue)
            .ToListAsync();
        activities.AddRange(openedResources.Select(resource =>
        {
            var occurredUtc = EnsureUtc(resource.LastOpenedUtc!.Value);
            return new LearningActivity
            {
                Type = LearningActivityType.ResourceRead,
                Title = resource.Title,
                Area = resource.Section,
                Detail = resource.Summary,
                SourceKind = "Resource",
                SourceId = resource.Id,
                OccurredUtc = occurredUtc,
                DeduplicationKey = LearningActivityRecorder.BuildDailyKey("resource", resource.Id.ToString(), occurredUtc)
            };
        }));

        var watchedVideos = await db.Videos
            .AsNoTracking()
            .Where(video => video.LastViewedUtc.HasValue)
            .ToListAsync();
        activities.AddRange(watchedVideos.Select(video =>
        {
            var occurredUtc = EnsureUtc(video.LastViewedUtc!.Value);
            return new LearningActivity
            {
                Type = LearningActivityType.VideoWatched,
                Title = video.Title,
                Area = video.ChannelTitle,
                Detail = video.Summary,
                SourceKind = "Video",
                SourceId = video.Id,
                OccurredUtc = occurredUtc,
                DeduplicationKey = LearningActivityRecorder.BuildDailyKey("video", video.Id.ToString(), occurredUtc)
            };
        }));

        var openedAnnouncements = await db.AnnouncementStates
            .AsNoTracking()
            .Where(state => state.LastOpenedUtc.HasValue)
            .ToListAsync();
        activities.AddRange(openedAnnouncements.Select(state =>
        {
            var occurredUtc = EnsureUtc(state.LastOpenedUtc!.Value);
            var sourceKey = LearningActivityRecorder.BuildStableSourceKey(state.Url);
            return new LearningActivity
            {
                Type = LearningActivityType.AnnouncementRead,
                Title = state.Title,
                Area = state.Topic,
                Detail = state.Summary,
                SourceKind = "Announcement",
                OccurredUtc = occurredUtc,
                DeduplicationKey = LearningActivityRecorder.BuildDailyKey("announcement", sourceKey, occurredUtc)
            };
        }));

        var existingReflections = (await db.Notes
                .AsNoTracking()
                .ToListAsync())
            .Where(note => !SeedNoteTitles.Contains(note.Title))
            .ToList();
        activities.AddRange(existingReflections.Select(note =>
        {
            var occurredUtc = EnsureUtc(note.UpdatedUtc);
            return new LearningActivity
            {
                Type = LearningActivityType.ReflectionAdded,
                Title = note.Title,
                Area = string.IsNullOrWhiteSpace(note.RelatedArea) ? note.Category : note.RelatedArea,
                Detail = note.Content,
                SourceKind = "Note",
                SourceId = note.Id,
                OccurredUtc = occurredUtc,
                DeduplicationKey = LearningActivityRecorder.BuildReflectionKey(note.Id, note.Content, occurredUtc)
            };
        }));

        var allRecorded = true;
        foreach (var activity in activities)
        {
            allRecorded &= await LearningActivityRecorder.TryRecordAsync(db, activity, logger);
        }

        if (!allRecorded)
        {
            logger.LogWarning("Learning history backfill was incomplete and will be retried on the next startup.");
            return;
        }

        db.AppMetadataEntries.Add(new AppMetadataEntry
        {
            Key = LearningHistoryBackfillMetadataKey,
            Value = $"Applied:{DateTime.UtcNow:O}",
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static async Task EnsureSeedVideoChannelsAsync(TrackerDbContext db)
    {
        var existingChannelKeys = await db.VideoChannels
            .AsNoTracking()
            .Select(channel => channel.Handle)
            .ToListAsync();

        var existingHandles = existingChannelKeys
            .Where(handle => !string.IsNullOrWhiteSpace(handle))
            .Select(handle => handle.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var channel in GetSeedVideoChannels())
        {
            if (existingHandles.Contains(channel.Handle.Trim()))
            {
                continue;
            }

            db.VideoChannels.Add(channel);
        }
    }

    private static bool IsPostgresProvider(TrackerDbContext db)
        => db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsTransientDatabaseException(Exception exception)
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

    private static string ResolveLegacySqliteConnectionString(StorageOptions storageOptions, IWebHostEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(storageOptions.LegacySqliteConnectionString))
        {
            return string.Empty;
        }

        return storageOptions.LegacySqliteConnectionString.Replace("%CONTENTROOT%", environment.ContentRootPath, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ImportLegacySqliteIfNeededAsync(TrackerDbContext db, string legacySqliteConnectionString, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(legacySqliteConnectionString))
        {
            return;
        }

        var alreadyImported = await db.AppMetadataEntries
            .AsNoTracking()
            .AnyAsync(entry => entry.Key == LegacySqliteImportMetadataKey);

        if (alreadyImported)
        {
            return;
        }

        var dataSource = TryGetSqliteDataSourcePath(legacySqliteConnectionString);
        if (string.IsNullOrWhiteSpace(dataSource) || !File.Exists(dataSource))
        {
            logger.LogInformation("Skipping legacy SQLite import because the source database was not found at {Path}.", dataSource);
            return;
        }

        await using var sqliteConnection = new SqliteConnection(legacySqliteConnectionString);
        await sqliteConnection.OpenAsync();

        logger.LogInformation("Importing legacy SQLite data from {Path}.", dataSource);

        await ImportTrainingItemsAsync(db, sqliteConnection);
        await ImportResourcesAsync(db, sqliteConnection);
        await ImportNotesAsync(db, sqliteConnection);
        await ImportVideoChannelsAsync(db, sqliteConnection);
        await ImportVideosAsync(db, sqliteConnection);

        db.AppMetadataEntries.Add(new AppMetadataEntry
        {
            Key = LegacySqliteImportMetadataKey,
            Value = $"Imported:{DateTime.UtcNow:O}",
            UpdatedUtc = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
        logger.LogInformation("Legacy SQLite import completed.");
    }

    private static async Task ImportLegacyLearningHistoryIfNeededAsync(TrackerDbContext db, string legacySqliteConnectionString, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(legacySqliteConnectionString))
        {
            return;
        }

        var alreadyImported = await db.AppMetadataEntries
            .AsNoTracking()
            .AnyAsync(entry => entry.Key == LegacySqliteLearningHistoryImportMetadataKey);

        if (alreadyImported)
        {
            return;
        }

        var dataSource = TryGetSqliteDataSourcePath(legacySqliteConnectionString);
        if (string.IsNullOrWhiteSpace(dataSource) || !File.Exists(dataSource))
        {
            return;
        }

        await using var sqliteConnection = new SqliteConnection(legacySqliteConnectionString);
        await sqliteConnection.OpenAsync();

        if (!await TableExistsAsync(sqliteConnection, "LearningActivities"))
        {
            logger.LogInformation("Legacy SQLite database has no learning history table to import yet.");
            return;
        }

        using var command = sqliteConnection.CreateCommand();
        command.CommandText = "SELECT * FROM LearningActivities ORDER BY OccurredUtc";

        var allRecorded = true;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var activity = new LearningActivity
            {
                Type = ReadEnum(reader, "Type", LearningActivityType.ProgressUpdated),
                Title = ReadString(reader, "Title"),
                Area = ReadString(reader, "Area"),
                Detail = ReadString(reader, "Detail"),
                SourceKind = ReadString(reader, "SourceKind"),
                SourceId = ReadNullableInt(reader, "SourceId"),
                ProgressPercent = ReadNullableInt(reader, "ProgressPercent"),
                EstimatedHours = ReadNullableDecimal(reader, "EstimatedHours"),
                OccurredUtc = ReadDateTime(reader, "OccurredUtc", DateTime.UtcNow),
                DeduplicationKey = ReadString(reader, "DeduplicationKey")
            };

            allRecorded &= await LearningActivityRecorder.TryRecordAsync(db, activity, logger);
        }

        if (!allRecorded)
        {
            logger.LogWarning("Legacy SQLite learning history import was incomplete and will be retried.");
            return;
        }

        db.AppMetadataEntries.Add(new AppMetadataEntry
        {
            Key = LegacySqliteLearningHistoryImportMetadataKey,
            Value = $"Imported:{DateTime.UtcNow:O}",
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        logger.LogInformation("Legacy SQLite learning history import completed.");
    }

    private static async Task ImportTrainingItemsAsync(TrackerDbContext db, SqliteConnection connection)
    {
        if (!await TableExistsAsync(connection, "TrainingItems"))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM TrainingItems";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetInt32(reader.GetOrdinal("Id"));
            var existing = await db.TrainingItems.FirstOrDefaultAsync(item => item.Id == id);
            var entity = existing ?? new TrainingItem { Id = id };

            entity.Title = ReadString(reader, "Title");
            entity.Domain = ReadString(reader, "Domain");
            entity.Category = ReadString(reader, "Category");
            entity.Description = ReadString(reader, "Description");
            entity.TargetDate = ReadDateTime(reader, "TargetDate", DateTime.UtcNow.Date);
            entity.Status = ReadEnum(reader, "Status", TrackerStatus.NotStarted);
            entity.Lane = ReadEnum(reader, "Lane", LearningLane.Core);
            entity.Type = ReadEnum(reader, "Type", TrainingItemType.Learning);
            entity.ProgressPercent = ReadInt(reader, "ProgressPercent");
            entity.EstimatedHours = ReadInt(reader, "EstimatedHours");
            entity.Priority = ReadInt(reader, "Priority");
            entity.ProjectDriven = ReadBool(reader, "ProjectDriven");
            entity.Notes = ReadString(reader, "Notes");
            entity.Evidence = ReadString(reader, "Evidence");
            entity.CreatedUtc = ReadDateTime(reader, "CreatedUtc", DateTime.UtcNow);
            entity.UpdatedUtc = ReadDateTime(reader, "UpdatedUtc", entity.CreatedUtc);
            entity.LastStatusChangedUtc = ReadNullableDateTime(reader, "LastStatusChangedUtc") ?? entity.UpdatedUtc;
            entity.CompletedUtc = ReadNullableDateTime(reader, "CompletedUtc");

            if (existing is null)
            {
                db.TrainingItems.Add(entity);
            }
        }
    }

    private static async Task ImportResourcesAsync(TrackerDbContext db, SqliteConnection connection)
    {
        if (!await TableExistsAsync(connection, "Resources"))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Resources";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetInt32(reader.GetOrdinal("Id"));
            var existing = await db.Resources.FirstOrDefaultAsync(resource => resource.Id == id);
            var entity = existing ?? new ResourceEntry { Id = id };

            entity.Title = ReadString(reader, "Title");
            entity.Section = ReadString(reader, "Section");
            entity.Url = ReadString(reader, "Url");
            entity.Kind = ReadEnum(reader, "Kind", ResourceKind.Documentation);
            entity.IsPinned = ReadBool(reader, "IsPinned");
            entity.Summary = ReadString(reader, "Summary");
            entity.Tags = ReadString(reader, "Tags");
            entity.Notes = ReadString(reader, "Notes");
            entity.SortOrder = ReadInt(reader, "SortOrder");
            entity.CreatedUtc = ReadDateTime(reader, "CreatedUtc", DateTime.UtcNow);
            entity.UpdatedUtc = ReadDateTime(reader, "UpdatedUtc", entity.CreatedUtc);
            entity.LastOpenedUtc = ReadNullableDateTime(reader, "LastOpenedUtc");

            if (existing is null)
            {
                db.Resources.Add(entity);
            }
        }
    }

    private static async Task ImportNotesAsync(TrackerDbContext db, SqliteConnection connection)
    {
        if (!await TableExistsAsync(connection, "Notes"))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Notes";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetInt32(reader.GetOrdinal("Id"));
            var existing = await db.Notes.FirstOrDefaultAsync(note => note.Id == id);
            var entity = existing ?? new NoteEntry { Id = id };

            entity.Title = ReadString(reader, "Title");
            entity.Category = ReadString(reader, "Category");
            entity.RelatedArea = ReadString(reader, "RelatedArea");
            entity.Tags = ReadString(reader, "Tags");
            entity.Content = ReadString(reader, "Content");
            entity.IsPinned = ReadBool(reader, "IsPinned");
            entity.CreatedUtc = ReadDateTime(reader, "CreatedUtc", DateTime.UtcNow);
            entity.UpdatedUtc = ReadDateTime(reader, "UpdatedUtc", entity.CreatedUtc);

            if (existing is null)
            {
                db.Notes.Add(entity);
            }
        }
    }

    private static async Task ImportVideoChannelsAsync(TrackerDbContext db, SqliteConnection connection)
    {
        if (!await TableExistsAsync(connection, "VideoChannels"))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM VideoChannels";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetInt32(reader.GetOrdinal("Id"));
            var existing = await db.VideoChannels.FirstOrDefaultAsync(channel => channel.Id == id);
            var entity = existing ?? new VideoChannel { Id = id };

            entity.DisplayName = ReadString(reader, "DisplayName");
            entity.Handle = ReadString(reader, "Handle");
            entity.ChannelId = ReadString(reader, "ChannelId");
            entity.ChannelUrl = ReadString(reader, "ChannelUrl");
            entity.Description = ReadString(reader, "Description");
            entity.ThumbnailUrl = ReadString(reader, "ThumbnailUrl");
            entity.IsSeeded = ReadBool(reader, "IsSeeded");
            entity.CreatedUtc = ReadDateTime(reader, "CreatedUtc", DateTime.UtcNow);
            entity.UpdatedUtc = ReadDateTime(reader, "UpdatedUtc", entity.CreatedUtc);
            entity.LastSyncedUtc = ReadNullableDateTime(reader, "LastSyncedUtc");

            if (existing is null)
            {
                db.VideoChannels.Add(entity);
            }
        }
    }

    private static async Task ImportVideosAsync(TrackerDbContext db, SqliteConnection connection)
    {
        if (!await TableExistsAsync(connection, "Videos"))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Videos";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetInt32(reader.GetOrdinal("Id"));
            var existing = await db.Videos.FirstOrDefaultAsync(video => video.Id == id);
            var entity = existing ?? new VideoEntry { Id = id };

            entity.ChannelId = ReadInt(reader, "ChannelId");
            entity.YouTubeVideoId = ReadString(reader, "YouTubeVideoId");
            entity.Title = ReadString(reader, "Title");
            entity.Url = ReadString(reader, "Url");
            entity.ThumbnailUrl = ReadString(reader, "ThumbnailUrl");
            entity.Summary = ReadString(reader, "Summary");
            entity.ChannelTitle = ReadString(reader, "ChannelTitle");
            entity.PublishedUtc = ReadDateTime(reader, "PublishedUtc", DateTime.UtcNow);
            entity.WatchState = ReadEnum(reader, "WatchState", VideoWatchState.Inbox);
            entity.CreatedUtc = ReadDateTime(reader, "CreatedUtc", DateTime.UtcNow);
            entity.UpdatedUtc = ReadDateTime(reader, "UpdatedUtc", entity.CreatedUtc);
            entity.LastViewedUtc = ReadNullableDateTime(reader, "LastViewedUtc");
            entity.LastSyncedUtc = ReadNullableDateTime(reader, "LastSyncedUtc");
            entity.RemovedUtc = ReadNullableDateTime(reader, "RemovedUtc");

            if (existing is null)
            {
                db.Videos.Add(entity);
            }
        }
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        command.Parameters.AddWithValue("$name", tableName);
        var result = await command.ExecuteScalarAsync();
        return result is not null;
    }

    private static string? TryGetSqliteDataSourcePath(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        return builder.DataSource;
    }

    private static async Task ResetIdentitySequencesAsync(TrackerDbContext db)
    {
        var statements = new[]
        {
            "SELECT setval(pg_get_serial_sequence('\"TrainingItems\"', 'Id'), COALESCE(MAX(\"Id\"), 1), MAX(\"Id\") IS NOT NULL) FROM \"TrainingItems\";",
            "SELECT setval(pg_get_serial_sequence('\"Resources\"', 'Id'), COALESCE(MAX(\"Id\"), 1), MAX(\"Id\") IS NOT NULL) FROM \"Resources\";",
            "SELECT setval(pg_get_serial_sequence('\"Notes\"', 'Id'), COALESCE(MAX(\"Id\"), 1), MAX(\"Id\") IS NOT NULL) FROM \"Notes\";",
            "SELECT setval(pg_get_serial_sequence('\"VideoChannels\"', 'Id'), COALESCE(MAX(\"Id\"), 1), MAX(\"Id\") IS NOT NULL) FROM \"VideoChannels\";",
            "SELECT setval(pg_get_serial_sequence('\"Videos\"', 'Id'), COALESCE(MAX(\"Id\"), 1), MAX(\"Id\") IS NOT NULL) FROM \"Videos\";",
            "SELECT setval(pg_get_serial_sequence('\"LearningActivities\"', 'Id'), COALESCE(MAX(\"Id\"), 1), MAX(\"Id\") IS NOT NULL) FROM \"LearningActivities\";"
        };

        foreach (var statement in statements)
        {
            await db.Database.ExecuteSqlRawAsync(statement);
        }
    }

    private static bool HasColumn(SqliteDataReader reader, string name)
    {
        for (var index = 0; index < reader.FieldCount; index++)
        {
            if (string.Equals(reader.GetName(index), name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadString(SqliteDataReader reader, string name)
    {
        if (!HasColumn(reader, name))
        {
            return string.Empty;
        }

        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
    }

    private static int ReadInt(SqliteDataReader reader, string name)
    {
        if (!HasColumn(reader, name))
        {
            return 0;
        }

        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0 : reader.GetInt32(ordinal);
    }

    private static int? ReadNullableInt(SqliteDataReader reader, string name)
    {
        if (!HasColumn(reader, name))
        {
            return null;
        }

        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static decimal? ReadNullableDecimal(SqliteDataReader reader, string name)
    {
        if (!HasColumn(reader, name))
        {
            return null;
        }

        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static bool ReadBool(SqliteDataReader reader, string name)
    {
        if (!HasColumn(reader, name))
        {
            return false;
        }

        var ordinal = reader.GetOrdinal(name);
        return !reader.IsDBNull(ordinal) && reader.GetBoolean(ordinal);
    }

    private static TEnum ReadEnum<TEnum>(SqliteDataReader reader, string name, TEnum fallback) where TEnum : struct, Enum
    {
        if (!HasColumn(reader, name))
        {
            return fallback;
        }

        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal))
        {
            return fallback;
        }

        return Enum.IsDefined(typeof(TEnum), reader.GetInt32(ordinal))
            ? (TEnum)Enum.ToObject(typeof(TEnum), reader.GetInt32(ordinal))
            : fallback;
    }

    private static DateTime ReadDateTime(SqliteDataReader reader, string name, DateTime fallback)
    {
        return ReadNullableDateTime(reader, name) ?? fallback;
    }

    private static DateTime? ReadNullableDateTime(SqliteDataReader reader, string name)
    {
        if (!HasColumn(reader, name))
        {
            return null;
        }

        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTime dateTime => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
            string text when DateTime.TryParse(text, out var parsed) => DateTime.SpecifyKind(parsed, DateTimeKind.Utc),
            long ticks => new DateTime(ticks, DateTimeKind.Utc),
            _ => null
        };
    }

    private static string BuildResourceKey(string title, string section)
        => string.Concat(section.Trim(), "::", title.Trim());

    private static DateTime UtcDate(int year, int month, int day)
        => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static IEnumerable<TrainingItem> GetSeedTrainingItems()
    {
        var today = DateTime.UtcNow.Date;
        var march = UtcDate(2026, 3, 17);
        var april = UtcDate(2026, 4, 18);
        var may = UtcDate(2026, 5, 16);
        var june = UtcDate(2026, 6, 20);
        var july = UtcDate(2026, 7, 18);
        var august = UtcDate(2026, 8, 15);
        var september = UtcDate(2026, 9, 12);

        return
        [
            new TrainingItem
            {
                Title = "Continue C# beginner series through arrays, lists, and beyond",
                Domain = "Coding Foundations",
                Category = "C#/.NET",
                Description = "Finish the beginner C# modules, capture key takeaways, and turn each milestone into a small coding exercise.",
                TargetDate = march,
                Lane = LearningLane.Core,
                Type = TrainingItemType.Learning,
                EstimatedHours = 4,
                Priority = 5,
                Notes = "Use the YouTube series as the structured path, then reinforce with one small console or Blazor exercise.",
                ProgressPercent = today > march ? 25 : 0
            },
            new TrainingItem
            {
                Title = "Build Python muscle memory for AI app workflows",
                Domain = "Coding Foundations",
                Category = "Python",
                Description = "Refresh Python fundamentals used in SDK samples, notebooks, APIs, and automation scripts for Azure AI work.",
                TargetDate = march.AddDays(10),
                Lane = LearningLane.Core,
                Type = TrainingItemType.Project,
                EstimatedHours = 4,
                Priority = 4,
                Notes = "Focus on virtual environments, requests, SDK auth, async basics, and data shaping.",
                ProjectDriven = true
            },
            new TrainingItem
            {
                Title = "Rapid-ramp Microsoft Foundry and the 2.x SDK",
                Domain = "Microsoft Foundry",
                Category = "Foundry",
                Description = "Learn the new portal, resource model, current SDK flow, model deployments, evaluations, tracing, and agent capabilities.",
                TargetDate = april,
                Lane = LearningLane.RapidRamp,
                Type = TrainingItemType.Lab,
                EstimatedHours = 6,
                Priority = 5,
                Notes = "Bias toward the latest Foundry docs, then compare with older samples only when needed for customer context.",
                ProjectDriven = true
            },
            new TrainingItem
            {
                Title = "Ground a RAG app with Azure AI Search",
                Domain = "Azure AI Search",
                Category = "RAG",
                Description = "Work through indexing, chunking, vector or hybrid retrieval, semantic ranker, and quality tradeoffs for enterprise AI apps.",
                TargetDate = may,
                Lane = LearningLane.Core,
                Type = TrainingItemType.Lab,
                EstimatedHours = 5,
                Priority = 5,
                Notes = "Document the chunking strategy and retrieval tuning choices you would explain to customers."
            },
            new TrainingItem
            {
                Title = "Adopt Microsoft Agent Framework patterns",
                Domain = "Agent Framework",
                Category = "Agents",
                Description = "Understand when to use agents vs workflows, tool use, MCP integration, memory, and orchestration patterns.",
                TargetDate = june,
                Lane = LearningLane.Core,
                Type = TrainingItemType.Project,
                EstimatedHours = 5,
                Priority = 4,
                Notes = "Keep examples aligned with Foundry-backed agents where possible."
            },
            new TrainingItem
            {
                Title = "Deep dive APIM, App Service, and Container Apps for AI apps",
                Domain = "App Innovation",
                Category = "Hosting and API layer",
                Description = "Map service selection guidance, deployment tradeoffs, auth, networking, and observability for customer architectures.",
                TargetDate = july,
                Lane = LearningLane.Core,
                Type = TrainingItemType.Project,
                EstimatedHours = 6,
                Priority = 5,
                Notes = "Capture decision points for when App Service beats Container Apps and vice versa."
            },
            new TrainingItem
            {
                Title = "Cover Service Bus, Logic Apps, and Redis integration patterns",
                Domain = "Integration Services",
                Category = "Messaging and workflow",
                Description = "Learn how AI apps connect to enterprise workflows, messaging, caching, and back-end systems.",
                TargetDate = july.AddDays(10),
                Lane = LearningLane.Stretch,
                Type = TrainingItemType.Learning,
                EstimatedHours = 4,
                Priority = 4,
                Notes = "Bias toward patterns that show durable orchestration, queue-based decoupling, and low-latency caching."
            },
            new TrainingItem
            {
                Title = "Run the Azure SRE Agent lab end to end",
                Domain = "Azure SRE Agent",
                Category = "Reliability",
                Description = "Deploy the lab, run the IT operations, developer, and workflow automation scenarios, then capture customer-facing takeaways.",
                TargetDate = august,
                Lane = LearningLane.RapidRamp,
                Type = TrainingItemType.Lab,
                EstimatedHours = 6,
                Priority = 5,
                Notes = "Use the lab to connect App Innovation architecture to agentic operations and incident response.",
                ProjectDriven = true
            },
            new TrainingItem
            {
                Title = "Productionize an AI app architecture",
                Domain = "Architecture and Operations",
                Category = "Security, cost, reliability",
                Description = "Document the security, monitoring, governance, and deployment story for AI apps on Azure.",
                TargetDate = august.AddDays(20),
                Lane = LearningLane.Core,
                Type = TrainingItemType.Project,
                EstimatedHours = 5,
                Priority = 4,
                Notes = "Include managed identity, telemetry, operational readiness, and customer tradeoff guidance."
            },
            new TrainingItem
            {
                Title = "Capstone 1: Foundry + Azure AI Search + App Service",
                Domain = "Capstones",
                Category = "Reference architecture",
                Description = "Build and document a polished end-to-end AI application with retrieval, observability, and deployment guidance.",
                TargetDate = september,
                Lane = LearningLane.Core,
                Type = TrainingItemType.Capstone,
                EstimatedHours = 8,
                Priority = 5,
                Notes = "Treat this as a reusable customer demo and architecture narrative."
            },
            new TrainingItem
            {
                Title = "Capstone 2: Agentic integration app with APIM and Service Bus",
                Domain = "Capstones",
                Category = "Integration architecture",
                Description = "Create an agent-enabled application pattern that includes API management, messaging, and operational guidance.",
                TargetDate = september.AddDays(7),
                Lane = LearningLane.Stretch,
                Type = TrainingItemType.Capstone,
                EstimatedHours = 8,
                Priority = 4,
                Notes = "Use this when customer demand shifts toward system integration and operational reliability."
            }
        ];
    }

    private static IEnumerable<TrainingItem> GetPlanExpansionTrainingItems()
    {
        var ai103Goal = CertificationCatalog.FindByKey("ai-103")?.CreateTrainingItem()
            ?? throw new InvalidOperationException("The AI-103 certification catalog entry is required.");
        ai103Goal.ProjectDriven = true;

        return
        [
            ai103Goal,
            new TrainingItem
            {
                Title = "Learn Microsoft Fabric fundamentals",
                Domain = "Microsoft Fabric",
                Category = "Fundamentals",
                Description = "Learn the Fabric workloads, OneLake, lakehouses, warehouses, real-time intelligence, data science, and Power BI at a true beginner level.",
                TargetDate = UtcDate(2026, 9, 11),
                Lane = LearningLane.Core,
                Type = TrainingItemType.Learning,
                EstimatedHours = 2,
                Priority = 5,
                Notes = "Start with the official introduction module before opening deeper workload-specific content."
            },
            new TrainingItem
            {
                Title = "Complete the Microsoft Fabric beginner path",
                Domain = "Microsoft Fabric",
                Category = "Learning path",
                Description = "Complete the official Get started with Microsoft Fabric path across lakehouses, warehouses, real-time intelligence, and data science.",
                TargetDate = UtcDate(2026, 10, 16),
                Lane = LearningLane.Core,
                Type = TrainingItemType.Learning,
                EstimatedHours = 8,
                Priority = 4,
                Notes = "Capture a one-page service map showing when each Fabric workload is the right fit."
            },
            new TrainingItem
            {
                Title = "Build a Microsoft Fabric lakehouse end to end",
                Domain = "Microsoft Fabric",
                Category = "Lakehouse",
                Description = "Complete the official lakehouse tutorial by creating a lakehouse, ingesting and transforming data, and producing a report.",
                TargetDate = UtcDate(2026, 11, 20),
                Lane = LearningLane.Core,
                Type = TrainingItemType.Project,
                EstimatedHours = 6,
                Priority = 4,
                ProjectDriven = true,
                Notes = "Save screenshots and architecture notes as evidence for later DP-600 or DP-700 preparation."
            },
            new TrainingItem
            {
                Title = "Explore Azure Databricks fundamentals",
                Domain = "Azure Databricks",
                Category = "Fundamentals",
                Description = "Learn Azure Databricks workspaces, notebooks, Spark, lakehouse concepts, Unity Catalog, and the main data and AI workloads.",
                TargetDate = UtcDate(2026, 9, 25),
                Lane = LearningLane.Core,
                Type = TrainingItemType.Learning,
                EstimatedHours = 2,
                Priority = 5,
                Notes = "Use the official Explore Azure Databricks module and complete its exercise."
            },
            new TrainingItem
            {
                Title = "Run a first Azure Databricks notebook",
                Domain = "Azure Databricks",
                Category = "Notebook quickstart",
                Description = "Provision or use an Azure Databricks workspace, open a notebook, query sample data with SQL or Python, and capture the workspace concepts learned.",
                TargetDate = UtcDate(2026, 10, 30),
                Lane = LearningLane.Core,
                Type = TrainingItemType.Lab,
                EstimatedHours = 4,
                Priority = 4,
                ProjectDriven = true,
                Notes = "Keep the first notebook small: load data, inspect a DataFrame, run SQL, and create one visualization."
            },
            new TrainingItem
            {
                Title = "Complete the Azure Databricks data engineering path",
                Domain = "Azure Databricks",
                Category = "Data engineering",
                Description = "Complete the official data analytics solution path covering Spark, Delta Lake, ETL, orchestration, streaming, and Unity Catalog.",
                TargetDate = UtcDate(2026, 12, 11),
                Lane = LearningLane.Core,
                Type = TrainingItemType.Learning,
                EstimatedHours = 8,
                Priority = 4,
                Notes = "Use Python and SQL together and record the concepts that map directly to the DP-750 study guide."
            },
            new TrainingItem
            {
                Title = "Build a Delta Lake ETL pipeline in Azure Databricks",
                Domain = "Azure Databricks",
                Category = "Delta Lake",
                Description = "Use the official MicrosoftLearning Databricks labs to build and explain a small Delta Lake ingestion and transformation pipeline.",
                TargetDate = UtcDate(2027, 1, 15),
                Lane = LearningLane.Stretch,
                Type = TrainingItemType.Lab,
                EstimatedHours = 6,
                Priority = 3,
                Notes = "This is the nice-to-have proof project after the core Databricks learning path."
            }
        ];
    }

    private static IEnumerable<VideoChannel> GetSeedVideoChannels()
    {
        return
        [
            new VideoChannel
            {
                DisplayName = "NTFAQGuy",
                Handle = "@NTFAQGuy",
                ChannelId = "seed:@NTFAQGuy",
                ChannelUrl = "https://www.youtube.com/@NTFAQGuy",
                Description = "Starter seed channel. Channel metadata is refreshed once the YouTube sync runs.",
                IsSeeded = true
            },
            new VideoChannel
            {
                DisplayName = "Microsoft Developer",
                Handle = "@MicrosoftDeveloper",
                ChannelId = "seed:@MicrosoftDeveloper",
                ChannelUrl = "https://www.youtube.com/@MicrosoftDeveloper",
                Description = "Starter seed channel. Channel metadata is refreshed once the YouTube sync runs.",
                IsSeeded = true
            },
            new VideoChannel
            {
                DisplayName = "Microsoft Mechanics",
                Handle = "@MSFTMechanics",
                ChannelId = "seed:@MSFTMechanics",
                ChannelUrl = "https://www.youtube.com/@MSFTMechanics",
                Description = "Starter seed channel. Channel metadata is refreshed once the YouTube sync runs.",
                IsSeeded = true
            }
        ];
    }

    private static IEnumerable<ResourceEntry> GetSeedResources()
    {
        return
        [
            new ResourceEntry { Title = "Foundry SDK overview (Python)", Section = "Microsoft Foundry", Url = "https://learn.microsoft.com/en-us/azure/foundry/how-to/develop/sdk-overview?pivots=programming-language-python", Kind = ResourceKind.Learn, IsPinned = true, SortOrder = 10, Summary = "Primary starting point for the latest Foundry 2.x SDK workflow and project-based development.", Tags = "foundry, sdk, python, latest" },
            new ResourceEntry { Title = "Foundry documentation home", Section = "Microsoft Foundry", Url = "https://learn.microsoft.com/en-us/azure/foundry/", Kind = ResourceKind.Documentation, SortOrder = 20, Summary = "Broader Foundry product documentation across projects, models, agents, evaluations, and observability.", Tags = "foundry, docs" },
            new ResourceEntry { Title = "GitHub Copilot documentation", Section = "GitHub Copilot", Url = "https://docs.github.com/en/copilot", Kind = ResourceKind.Documentation, IsPinned = true, SortOrder = 10, Summary = "Keep customer-ready Copilot knowledge current across chat, coding workflows, and governance.", Tags = "copilot, docs, github" },
            new ResourceEntry { Title = "App Service documentation", Section = "App Service", Url = "https://learn.microsoft.com/en-us/azure/app-service/", Kind = ResourceKind.Documentation, SortOrder = 10, Summary = "Core hosting reference for web apps, APIs, auth, deployment, and scaling on App Service.", Tags = "app service, hosting" },
            new ResourceEntry { Title = "Container Apps documentation", Section = "Container Apps", Url = "https://learn.microsoft.com/en-us/azure/container-apps/", Kind = ResourceKind.Documentation, SortOrder = 10, Summary = "Managed container platform reference for APIs, event-driven apps, and background workloads.", Tags = "container apps, hosting, containers" },
            new ResourceEntry { Title = "Azure API Management documentation", Section = "APIM", Url = "https://learn.microsoft.com/en-us/azure/api-management/", Kind = ResourceKind.Documentation, SortOrder = 10, Summary = "Use for API governance, security, throttling, mediation, and AI gateway patterns.", Tags = "apim, api management" },
            new ResourceEntry { Title = "Azure AI Search documentation", Section = "Azure AI Search", Url = "https://learn.microsoft.com/en-us/azure/search/", Kind = ResourceKind.Documentation, IsPinned = true, SortOrder = 10, Summary = "Main landing page for search, indexing, vector search, and RAG design guidance.", Tags = "search, rag, vector" },
            new ResourceEntry { Title = "Microsoft Agent Framework overview", Section = "Agent Framework", Url = "https://learn.microsoft.com/agent-framework/overview/", Kind = ResourceKind.Learn, IsPinned = true, SortOrder = 10, Summary = "Official landing page for agents, workflows, tools, and provider integrations in C# and Python.", Tags = "agent framework, agents, workflows" },
            new ResourceEntry { Title = "Microsoft Agent Framework Dev Blog", Section = "Agent Framework", Url = "https://devblogs.microsoft.com/agent-framework/", Kind = ResourceKind.Documentation, SortOrder = 15, Summary = "Official Microsoft Agent Framework blog with announcements, deep dives, samples, and GitHub Copilot SDK integration posts.", Tags = "agent framework, microsoft, devblogs, copilot sdk" },
            new ResourceEntry { Title = "Service Bus documentation", Section = "Service Bus", Url = "https://learn.microsoft.com/en-us/azure/service-bus-messaging/", Kind = ResourceKind.Documentation, SortOrder = 10, Summary = "Messaging patterns for durable decoupling, asynchronous work, and enterprise integration.", Tags = "service bus, messaging" },
            new ResourceEntry { Title = "Logic Apps documentation", Section = "Logic Apps", Url = "https://learn.microsoft.com/en-us/azure/logic-apps/", Kind = ResourceKind.Documentation, SortOrder = 10, Summary = "Workflow automation guidance for connecting AI apps to enterprise systems.", Tags = "logic apps, workflow" },
            new ResourceEntry { Title = "Azure Cache for Redis documentation", Section = "Redis", Url = "https://learn.microsoft.com/en-us/azure/azure-cache-for-redis/", Kind = ResourceKind.Documentation, SortOrder = 10, Summary = "Caching guidance for low-latency access patterns and session or memory scenarios.", Tags = "redis, cache" },
            new ResourceEntry { Title = "Azure SRE Agent GA announcement", Section = "Azure SRE Agent", Url = "https://techcommunity.microsoft.com/blog/appsonazureblog/announcing-general-availability-for-the-azure-sre-agent/4500682", Kind = ResourceKind.Documentation, IsPinned = true, SortOrder = 10, Summary = "Practical framing for how Azure SRE Agent supports diagnostics, knowledge reuse, and governed remediation.", Tags = "sre agent, reliability, operations" },
            new ResourceEntry { Title = "Azure SRE Agent hands-on lab", Section = "Azure SRE Agent", Url = "https://github.com/dm-chelupati/sre-agent-lab/tree/main?tab=readme-ov-file", Kind = ResourceKind.Lab, IsPinned = true, SortOrder = 20, Summary = "Deployable lab for incident investigation, code-aware diagnosis, and GitHub issue triage.", Tags = "sre agent, github, lab, azd" },
            new ResourceEntry { Title = "C# beginner series", Section = "Coding Foundations", Url = "https://www.youtube.com/watch?v=9THmGiSPjBQ&list=PLdo4fOcmZ0oULFjxrOagaERVAMbmG20Xe", Kind = ResourceKind.Video, SortOrder = 10, Summary = "Continue from arrays and lists, then carry the learning into .NET UI and services work.", Tags = "c#, beginner, dotnet, video" },
            new ResourceEntry { Title = "Python for beginners learning path", Section = "Coding Foundations", Url = "https://learn.microsoft.com/en-us/training/paths/beginner-python/", Kind = ResourceKind.Learn, SortOrder = 20, Summary = "Structured Microsoft Learn path for Python syntax, functions, collections, files, and practical exercises.", Tags = "python, fundamentals, beginner, learn" },
            new ResourceEntry { Title = "Python tutorial", Section = "Coding Foundations", Url = "https://docs.python.org/3/tutorial/", Kind = ResourceKind.Documentation, SortOrder = 30, Summary = "Official Python tutorial covering control flow, data structures, modules, and classes for day-to-day coding fluency.", Tags = "python, fundamentals, tutorial, docs" },
            new ResourceEntry { Title = "Build a RAG solution with Azure AI Search", Section = "Azure AI Search", Url = "https://learn.microsoft.com/en-us/azure/search/retrieval-augmented-generation-overview", Kind = ResourceKind.Learn, SortOrder = 20, Summary = "RAG-specific guidance for grounding apps with Azure AI Search, including indexing and retrieval patterns.", Tags = "search, rag, grounding, ai search" },
            new ResourceEntry { Title = "Well-Architected Framework", Section = "Architecture and Operations", Url = "https://learn.microsoft.com/en-us/azure/well-architected/", Kind = ResourceKind.Documentation, SortOrder = 10, Summary = "Use this to shape production guidance for security, reliability, operational excellence, performance, and cost.", Tags = "architecture, reliability, security, cost, operations" },
            new ResourceEntry { Title = "Azure Monitor and Application Insights", Section = "Architecture and Operations", Url = "https://learn.microsoft.com/en-us/azure/azure-monitor/app/app-insights-overview", Kind = ResourceKind.Documentation, SortOrder = 20, Summary = "Telemetry and observability foundation for productionizing AI and App Innovation workloads.", Tags = "monitoring, telemetry, application insights, observability" },
            new ResourceEntry { Title = "Choose between App Service, Container Apps, and AKS", Section = "App Innovation", Url = "https://learn.microsoft.com/en-us/azure/architecture/guide/technology-choices/compute-decision-tree", Kind = ResourceKind.Documentation, SortOrder = 10, Summary = "Decision guidance for selecting the right Azure compute platform for APIs, web apps, containers, and AI workloads.", Tags = "app innovation, app service, container apps, hosting, architecture" },
            new ResourceEntry { Title = "Develop generative AI apps in Azure", Section = "Learning Paths", Url = "https://learn.microsoft.com/en-us/training/paths/develop-ai-solutions-azure-openai/", Kind = ResourceKind.Learn, SortOrder = 10, Summary = "Structured Microsoft Learn path to complement Foundry and AI app development work.", Tags = "learn, azure ai, path" },
            new ResourceEntry { Title = "Microsoft Fabric documentation", Section = "Microsoft Fabric", Url = "https://learn.microsoft.com/en-us/fabric/", Kind = ResourceKind.Documentation, SortOrder = 10, Summary = "Official documentation hub for Fabric workloads, administration, governance, and architecture.", Tags = "fabric, onelake, analytics, beginner" },
            new ResourceEntry { Title = "Introduction to Microsoft Fabric", Section = "Microsoft Fabric", Url = "https://learn.microsoft.com/en-us/training/modules/introduction-end-analytics-use-microsoft-fabric/", Kind = ResourceKind.Learn, IsPinned = true, SortOrder = 20, Summary = "Short beginner module explaining Fabric's end-to-end analytics workloads and OneLake.", Tags = "fabric, fundamentals, beginner, onelake" },
            new ResourceEntry { Title = "Get started with Microsoft Fabric learning path", Section = "Microsoft Fabric", Url = "https://learn.microsoft.com/en-us/training/paths/get-started-fabric/", Kind = ResourceKind.Learn, SortOrder = 30, Summary = "Official beginner path covering lakehouses, warehouses, real-time intelligence, and data science.", Tags = "fabric, learning path, lakehouse, warehouse, beginner" },
            new ResourceEntry { Title = "Microsoft Fabric lakehouse tutorial", Section = "Microsoft Fabric", Url = "https://learn.microsoft.com/en-us/fabric/data-engineering/tutorial-build-lakehouse", Kind = ResourceKind.Lab, SortOrder = 40, Summary = "End-to-end beginner project for ingesting, transforming, and reporting on data in a Fabric lakehouse.", Tags = "fabric, lakehouse, tutorial, project, lab" },
            new ResourceEntry { Title = "Microsoft Fabric end-to-end tutorials", Section = "Microsoft Fabric", Url = "https://learn.microsoft.com/en-us/fabric/fundamentals/end-to-end-tutorials", Kind = ResourceKind.Lab, SortOrder = 50, Summary = "Scenario-based tutorials across Fabric data engineering, warehousing, science, and real-time workloads.", Tags = "fabric, tutorials, hands-on" },
            new ResourceEntry { Title = "Azure Databricks documentation", Section = "Azure Databricks", Url = "https://learn.microsoft.com/en-us/azure/databricks/", Kind = ResourceKind.Documentation, SortOrder = 10, Summary = "Official Azure Databricks reference for workspaces, notebooks, Spark, Delta Lake, Unity Catalog, and operations.", Tags = "databricks, spark, delta lake, unity catalog" },
            new ResourceEntry { Title = "Explore Azure Databricks module", Section = "Azure Databricks", Url = "https://learn.microsoft.com/en-us/training/modules/explore-azure-databricks/", Kind = ResourceKind.Learn, IsPinned = true, SortOrder = 20, Summary = "Beginner module covering the Azure Databricks platform, workloads, governance, and a hands-on exercise.", Tags = "databricks, fundamentals, beginner, module" },
            new ResourceEntry { Title = "Azure Databricks getting started tutorials", Section = "Azure Databricks", Url = "https://learn.microsoft.com/en-us/azure/databricks/getting-started/", Kind = ResourceKind.Lab, SortOrder = 30, Summary = "Official quickstarts for opening a workspace, running notebooks, querying data, and building first workflows.", Tags = "databricks, quickstart, notebook, tutorial" },
            new ResourceEntry { Title = "Azure Databricks data engineering learning path", Section = "Azure Databricks", Url = "https://learn.microsoft.com/en-us/training/paths/data-engineer-azure-databricks/", Kind = ResourceKind.Learn, SortOrder = 40, Summary = "Structured path covering Spark, SQL, Delta Lake, ETL, orchestration, streaming, and Unity Catalog.", Tags = "databricks, data engineering, spark, delta lake, learning path" },
            new ResourceEntry { Title = "Microsoft Learn Azure Databricks labs", Section = "Azure Databricks", Url = "https://github.com/MicrosoftLearning/mslearn-databricks", Kind = ResourceKind.Lab, SortOrder = 50, Summary = "Official lab assets for the Microsoft Learn Azure Databricks modules and end-to-end exercises.", Tags = "databricks, github, labs, delta lake, etl" },
            new ResourceEntry { Title = "Free Azure Databricks training", Section = "Azure Databricks", Url = "https://learn.microsoft.com/en-us/azure/databricks/getting-started/free-training", Kind = ResourceKind.Learn, SortOrder = 60, Summary = "Free Databricks Academy courses and webinars for additional platform and Spark foundations.", Tags = "databricks, academy, free training, beginner" },
            new ResourceEntry { Title = "AI-103 certification page", Section = "Certifications", Url = "https://learn.microsoft.com/en-us/credentials/certifications/azure-ai-apps-and-agents-developer-associate/", Kind = ResourceKind.Learn, IsPinned = true, SortOrder = 10, Summary = "Official Azure AI Apps and Agents Developer Associate credential page.", Tags = "certification, ai-103, azure ai, agents, foundry" },
            new ResourceEntry { Title = "AI-103 study guide", Section = "Certifications", Url = "https://learn.microsoft.com/en-us/credentials/certifications/resources/study-guides/ai-103", Kind = ResourceKind.Learn, SortOrder = 20, Summary = "Official AI-103 skills outline and preparation guidance.", Tags = "certification, ai-103, study guide, azure ai, agents" }
        ];
    }

    private static IEnumerable<NoteEntry> GetSeedNotes()
    {
        return
        [
            new NoteEntry
            {
                Title = "Operating model for this tracker",
                Category = "System",
                RelatedArea = "Execution cadence",
                Tags = "weekly review, evidence, cadence",
                IsPinned = true,
                Content = "Use the Core lane as the minimum weekly commitment. When project pressure rises, switch to the matching Rapid-Ramp items and log the learning outcome as evidence. When time opens up, pull from Stretch items."
            },
            new NoteEntry
            {
                Title = "Customer-facing architecture note template",
                Category = "Architecture",
                RelatedArea = "Reusable pattern",
                Tags = "tradeoffs, decision log",
                Content = "For each major topic, capture: problem statement, service choice, rejected alternatives, security considerations, operational concerns, cost considerations, and how to explain the pattern to a customer."
            },
            new NoteEntry
            {
                Title = "Hands-on lab reflection template",
                Category = "Labs",
                RelatedArea = "Evidence",
                Tags = "retrospective, demos",
                Content = "When you finish a lab, note what worked, where it broke, the architecture lesson, the customer story, and what would be required for production readiness."
            }
        ];
    }

    private static readonly JsonSerializerOptions LearningRadarJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static async Task ImportLearningRadarItemsAsync(TrackerDbContext db, IWebHostEnvironment environment, ILogger logger)
    {
        var candidatePaths = new[]
        {
            Path.Combine(environment.ContentRootPath, "Data", "SeedData", "learning-radar.json"),
            Path.Combine(AppContext.BaseDirectory, "Data", "SeedData", "learning-radar.json")
        };

        var path = candidatePaths.FirstOrDefault(File.Exists);
        if (path is null)
        {
            return;
        }

        LearningRadarDocument? document;
        try
        {
            await using var stream = File.OpenRead(path);
            document = await JsonSerializer.DeserializeAsync<LearningRadarDocument>(stream, LearningRadarJsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Skipping learning radar import because {Path} could not be parsed.", path);
            return;
        }

        if (document?.Items is not { Count: > 0 })
        {
            return;
        }

        var existingTitles = (await db.TrainingItems
            .AsNoTracking()
            .Select(item => item.Title)
            .ToListAsync())
            .Concat(db.TrainingItems.Local.Select(item => item.Title))
            .Select(title => title.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in document.Items)
        {
            if (string.IsNullOrWhiteSpace(entry.Title))
            {
                continue;
            }

            var title = entry.Title.Trim();
            if (title.Length > 140 || !existingTitles.Add(title))
            {
                continue;
            }

            var noteLines = new[]
            {
                entry.Notes?.Trim(),
                string.IsNullOrWhiteSpace(entry.Link) ? null : $"Link: {entry.Link.Trim()}",
                string.IsNullOrWhiteSpace(entry.Source) ? null : $"Source: {entry.Source.Trim()}",
                string.IsNullOrWhiteSpace(entry.AddedOn) ? null : $"Added by learning radar on {entry.AddedOn.Trim()}."
            };

            var notes = string.Join('\n', noteLines.Where(line => !string.IsNullOrWhiteSpace(line)));

            db.TrainingItems.Add(new TrainingItem
            {
                Title = title,
                Domain = Truncate(string.IsNullOrWhiteSpace(entry.Domain) ? "Learning Radar" : entry.Domain.Trim(), 80),
                Category = Truncate(entry.Category?.Trim() ?? string.Empty, 80),
                Description = Truncate(entry.Description?.Trim() ?? string.Empty, 1500),
                TargetDate = ParseDateOrDefault(entry.TargetDate, DateTime.UtcNow.Date.AddDays(14)),
                Lane = ParseEnumOrDefault(entry.Lane, LearningLane.Stretch),
                Type = ParseEnumOrDefault(entry.Type, TrainingItemType.Learning),
                EstimatedHours = entry.EstimatedHours is >= 0.5m and <= 40m ? entry.EstimatedHours.Value : 2,
                Priority = entry.Priority is >= 1 and <= 5 ? entry.Priority.Value : 3,
                Notes = Truncate(notes, 4000)
            });

            logger.LogInformation("Learning radar import queued new training item: {Title}", title);
        }
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static DateTime ParseDateOrDefault(string? value, DateTime fallback)
        => DateTime.TryParse(value, out var parsed) ? parsed.Date : fallback;

    private static TEnum ParseEnumOrDefault<TEnum>(string? value, TEnum fallback) where TEnum : struct
        => Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;

    private sealed class LearningRadarDocument
    {
        [JsonPropertyName("items")]
        public List<LearningRadarItem> Items { get; set; } = [];
    }

    private sealed class LearningRadarItem
    {
        public string? Title { get; set; }
        public string? Domain { get; set; }
        public string? Category { get; set; }
        public string? Description { get; set; }
        public string? TargetDate { get; set; }
        public string? Lane { get; set; }
        public string? Type { get; set; }
        public decimal? EstimatedHours { get; set; }
        public int? Priority { get; set; }
        public string? Link { get; set; }
        public string? Source { get; set; }
        public string? Notes { get; set; }
        public string? AddedOn { get; set; }
    }
}
