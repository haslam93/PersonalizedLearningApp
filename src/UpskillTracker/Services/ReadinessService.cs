using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using UpskillTracker.Data;

namespace UpskillTracker.Services;

public sealed class ReadinessService(IDbContextFactory<TrackerDbContext> dbFactory, PinSessionService sessions)
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    public async Task<ReadinessResponse> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ready = sessions.IsConfigured && await IsDatabaseReadyAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new ReadinessResponse(ready ? "healthy" : "unhealthy", BuildVersion.Version, BuildVersion.Commit);
    }

    private async Task<bool> IsDatabaseReadyAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ProbeTimeout);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(deadline.Token);
            db.Database.SetCommandTimeout((int)ProbeTimeout.TotalSeconds);

            // Check the live connection and required tables without returning any learning data.
            await db.TrainingItems.Select(_ => 1)
                .Concat(db.Resources.Select(_ => 1))
                .Concat(db.Notes.Select(_ => 1))
                .Concat(db.VideoChannels.Select(_ => 1))
                .Concat(db.Videos.Select(_ => 1))
                .Concat(db.AnnouncementStates.Select(_ => 1))
                .Concat(db.GitHubAuthSessions.Select(_ => 1))
                .Concat(db.AppMetadataEntries.Select(_ => 1))
                .Concat(db.LearningActivities.Select(_ => 1))
                .Take(1)
                .ToListAsync(deadline.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (DbException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (InvalidOperationException exception) when (exception.InnerException is DbException or TimeoutException)
        {
            // Npgsql's non-retrying EF execution strategy wraps transient connection failures.
            return false;
        }
    }

    private static class BuildVersion
    {
        public static readonly string Version = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        private static readonly string[] Parts = Version.Split('+', 2);

        public static readonly string Commit = Parts.Length == 2 &&
            Parts[1].Length is 40 or 64 && Parts[1].All(char.IsAsciiHexDigit)
                ? Parts[1].ToLowerInvariant()
                : "unknown";
    }
}

public sealed record ReadinessResponse(string Status, string Version, string Commit);
