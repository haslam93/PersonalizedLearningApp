using Microsoft.EntityFrameworkCore;
using UpskillTracker.Models;

namespace UpskillTracker.Data;

public class TrackerDbContext(DbContextOptions<TrackerDbContext> options) : DbContext(options)
{
    public DbSet<TrainingItem> TrainingItems => Set<TrainingItem>();

    public DbSet<ResourceEntry> Resources => Set<ResourceEntry>();

    public DbSet<NoteEntry> Notes => Set<NoteEntry>();

    public DbSet<VideoChannel> VideoChannels => Set<VideoChannel>();

    public DbSet<VideoEntry> Videos => Set<VideoEntry>();

    public DbSet<AnnouncementState> AnnouncementStates => Set<AnnouncementState>();

    public DbSet<GitHubAuthSession> GitHubAuthSessions => Set<GitHubAuthSession>();

    public DbSet<AppMetadataEntry> AppMetadataEntries => Set<AppMetadataEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TrainingItem>()
            .Property(item => item.EstimatedHours)
            .HasPrecision(5, 1);

        modelBuilder.Entity<TrainingItem>()
            .HasIndex(item => new { item.TargetDate, item.Status });

        modelBuilder.Entity<ResourceEntry>()
            .HasIndex(resource => new { resource.Section, resource.SortOrder });

        modelBuilder.Entity<ResourceEntry>()
            .HasIndex(resource => resource.LastOpenedUtc);

        modelBuilder.Entity<NoteEntry>()
            .HasIndex(note => note.CreatedUtc);

        modelBuilder.Entity<TrainingItem>()
            .HasIndex(item => item.LastStatusChangedUtc);

        modelBuilder.Entity<VideoChannel>()
            .HasIndex(channel => channel.ChannelId)
            .IsUnique();

        modelBuilder.Entity<VideoChannel>()
            .HasIndex(channel => channel.Handle)
            .IsUnique();

        modelBuilder.Entity<VideoEntry>()
            .HasIndex(video => video.YouTubeVideoId)
            .IsUnique();

        modelBuilder.Entity<VideoEntry>()
            .HasIndex(video => new { video.WatchState, video.PublishedUtc });

        modelBuilder.Entity<VideoEntry>()
            .HasIndex(video => new { video.ChannelId, video.PublishedUtc });

        modelBuilder.Entity<AnnouncementState>()
            .HasIndex(state => new { state.Stream, state.IsSeen, state.PublishedUtc });

        modelBuilder.Entity<AnnouncementState>()
            .HasIndex(state => new { state.Topic, state.Source });

        modelBuilder.Entity<GitHubAuthSession>()
            .HasIndex(session => session.ExpiresUtc);

        modelBuilder.Entity<VideoEntry>()
            .HasOne(video => video.Channel)
            .WithMany(channel => channel.Videos)
            .HasForeignKey(video => video.ChannelId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
