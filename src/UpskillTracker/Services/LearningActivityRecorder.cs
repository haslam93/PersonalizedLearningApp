using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using UpskillTracker.Data;
using UpskillTracker.Models;

namespace UpskillTracker.Services;

public static class LearningActivityRecorder
{
    public static async Task<bool> TryRecordAsync(TrackerDbContext db, LearningActivity activity, ILogger logger)
    {
        Normalize(activity);

        try
        {
            if (db.Database.IsNpgsql())
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO "LearningActivities"
                        ("Type", "Title", "Area", "Detail", "SourceKind", "SourceId", "ProgressPercent", "EstimatedHours", "OccurredUtc", "DeduplicationKey")
                    VALUES
                        ({(int)activity.Type}, {activity.Title}, {activity.Area}, {activity.Detail}, {activity.SourceKind}, {activity.SourceId}, {activity.ProgressPercent}, {activity.EstimatedHours}, {activity.OccurredUtc}, {activity.DeduplicationKey})
                    ON CONFLICT ("DeduplicationKey") DO NOTHING;
                    """);
                return true;
            }

            if (db.Database.IsSqlite())
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT OR IGNORE INTO LearningActivities
                        (Type, Title, Area, Detail, SourceKind, SourceId, ProgressPercent, EstimatedHours, OccurredUtc, DeduplicationKey)
                    VALUES
                        ({(int)activity.Type}, {activity.Title}, {activity.Area}, {activity.Detail}, {activity.SourceKind}, {activity.SourceId}, {activity.ProgressPercent}, {activity.EstimatedHours}, {activity.OccurredUtc}, {activity.DeduplicationKey});
                    """);
                return true;
            }

            if (!await db.LearningActivities.AnyAsync(existing => existing.DeduplicationKey == activity.DeduplicationKey))
            {
                db.LearningActivities.Add(activity);
                await db.SaveChangesAsync();
            }

            return true;
        }
        catch (DbException ex)
        {
            logger.LogError(ex, "Could not record learning activity {DeduplicationKey}. The primary user action was preserved.", activity.DeduplicationKey);
            return false;
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "Could not record learning activity {DeduplicationKey}. The primary user action was preserved.", activity.DeduplicationKey);
            return false;
        }
    }

    public static string BuildDailyKey(string sourceKind, string sourceKey, DateTime occurredUtc)
        => $"{sourceKind.Trim().ToLowerInvariant()}:{sourceKey.Trim().ToLowerInvariant()}:{occurredUtc:yyyyMMdd}";

    public static string BuildTrainingKey(int itemId, LearningActivityType type, DateTime occurredUtc, int? progressPercent = null)
        => $"training:{itemId}:{type.ToString().ToLowerInvariant()}:{occurredUtc:yyyyMMddHHmmss}:{progressPercent?.ToString() ?? "na"}";

    public static string BuildReflectionKey(int noteId, string content, DateTime occurredUtc)
    {
        var hash = BuildStableSourceKey(content);
        return $"note:{noteId}:reflection:{occurredUtc:yyyyMMdd}:{hash}";
    }

    public static string BuildStableSourceKey(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)))[..16].ToLowerInvariant();

    private static void Normalize(LearningActivity activity)
    {
        activity.Title = Truncate(activity.Title, 180);
        activity.Area = Truncate(activity.Area, 100);
        activity.Detail = Truncate(activity.Detail, 1000);
        activity.SourceKind = Truncate(activity.SourceKind, 40);
        activity.DeduplicationKey = Truncate(activity.DeduplicationKey, 180);
        activity.OccurredUtc = activity.OccurredUtc.Kind == DateTimeKind.Utc
            ? activity.OccurredUtc
            : DateTime.SpecifyKind(activity.OccurredUtc, DateTimeKind.Utc);
    }

    private static string Truncate(string value, int maxLength)
        => string.IsNullOrWhiteSpace(value) || value.Length <= maxLength
            ? value
            : value[..maxLength];
}
