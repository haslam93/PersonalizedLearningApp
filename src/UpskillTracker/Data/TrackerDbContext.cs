using Microsoft.EntityFrameworkCore;
using UpskillTracker.Models;

namespace UpskillTracker.Data;

public class TrackerDbContext(DbContextOptions<TrackerDbContext> options) : DbContext(options)
{
    public DbSet<TrainingItem> TrainingItems => Set<TrainingItem>();

    public DbSet<ResourceEntry> Resources => Set<ResourceEntry>();

    public DbSet<NoteEntry> Notes => Set<NoteEntry>();

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
    }
}
