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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TrainingItem>()
            .Property(item => item.EstimatedHours)
            .HasPrecision(5, 1);

        modelBuilder.Entity<TrainingItem>()
            .HasIndex(item => new { item.TargetDate, item.Status });

        modelBuilder.Entity<ResourceEntry>()
            .HasIndex(resource => new { resource.Section, resource.SortOrder });

        modelBuilder.Entity<NoteEntry>()
            .HasIndex(note => note.CreatedUtc);

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

        modelBuilder.Entity<VideoEntry>()
            .HasOne(video => video.Channel)
            .WithMany(channel => channel.Videos)
            .HasForeignKey(video => video.ChannelId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
