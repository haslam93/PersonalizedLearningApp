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

    public static async Task InitializeAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TrackerDbContext>>();
        var environment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInitializer");
        var storageOptions = scope.ServiceProvider.GetRequiredService<IOptions<StorageOptions>>().Value;
        await using var db = await dbFactory.CreateDbContextAsync();

        await db.Database.EnsureCreatedAsync();
        await EnsureVideoSchemaAsync(db);

        if (IsPostgresProvider(db) && storageOptions.EnableLegacySqliteImport)
        {
            var legacySqliteConnectionString = ResolveLegacySqliteConnectionString(storageOptions, environment);
            await ImportLegacySqliteIfNeededAsync(db, legacySqliteConnectionString, logger);
        }

        if (!await db.TrainingItems.AnyAsync())
        {
            db.TrainingItems.AddRange(GetSeedTrainingItems());
        }

        await EnsureSeedResourcesAsync(db);
        await EnsureSeedVideoChannelsAsync(db);

        if (!await db.Notes.AnyAsync())
        {
            db.Notes.AddRange(GetSeedNotes());
        }

        await db.SaveChangesAsync();

        if (IsPostgresProvider(db))
        {
            await ResetIdentitySequencesAsync(db);
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
            "SELECT setval(pg_get_serial_sequence('\"Videos\"', 'Id'), COALESCE(MAX(\"Id\"), 1), MAX(\"Id\") IS NOT NULL) FROM \"Videos\";"
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

    private static IEnumerable<TrainingItem> GetSeedTrainingItems()
    {
        var today = DateTime.UtcNow.Date;
        var march = new DateTime(2026, 3, 17);
        var april = new DateTime(2026, 4, 18);
        var may = new DateTime(2026, 5, 16);
        var june = new DateTime(2026, 6, 20);
        var july = new DateTime(2026, 7, 18);
        var august = new DateTime(2026, 8, 15);
        var september = new DateTime(2026, 9, 12);

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
            new ResourceEntry { Title = "Develop generative AI apps in Azure", Section = "Learning Paths", Url = "https://learn.microsoft.com/en-us/training/paths/develop-ai-solutions-azure-openai/", Kind = ResourceKind.Learn, SortOrder = 10, Summary = "Structured Microsoft Learn path to complement Foundry and AI app development work.", Tags = "learn, azure ai, path" }
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
}
