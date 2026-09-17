using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using UpskillTracker.Data;
using UpskillTracker.Models;

namespace UpskillTracker.Tests;

public sealed class KarpathyLearningPlanTests
{
    private const string MetadataKey = "karpathy-llm-learning-plan-v1";

    [Fact]
    public async Task Fresh_plan_contains_ordered_long_term_projects_and_official_resources()
    {
        await using var app = new PortalApplicationFactory();
        await using var db = await app.OpenDatabaseAsync();
        var items = await db.TrainingItems.Where(item => item.Category == "Andrej Karpathy")
            .OrderBy(item => item.TargetDate).ToListAsync();

        Assert.Equal(4, items.Count);
        Assert.Equal(new decimal[] { 8, 16, 10, 24 }, items.Select(item => item.EstimatedHours));
        Assert.Equal(new[] { TrainingItemType.Lab, TrainingItemType.Project, TrainingItemType.Lab, TrainingItemType.Capstone },
            items.Select(item => item.Type));
        Assert.Equal(new[] { new DateTime(2027, 1, 29), new DateTime(2027, 3, 26), new DateTime(2027, 5, 7), new DateTime(2027, 7, 30) },
            items.Select(item => item.TargetDate));
        var repositories = new[] { "micrograd", "ng-video-lecture", "minbpe", "build-nanogpt" };
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            Validator.ValidateObject(item, new ValidationContext(item), validateAllProperties: true);
            Assert.Equal(LearningLane.Stretch, item.Lane);
            Assert.Equal(TrackerStatus.NotStarted, item.Status);
            Assert.Equal(0, item.ProgressPercent);
            Assert.False(item.ProjectDriven);
            Assert.Contains("Prerequisites:", item.Notes);
            Assert.Contains("Planning estimate:", item.Notes);
            Assert.Contains("Done when:", item.Notes);
            Assert.Contains("https://www.youtube.com/watch?v=", item.Notes);
            var url = $"https://github.com/karpathy/{repositories[index]}";
            Assert.Contains(url, item.Notes);
            Assert.True(await db.Resources.AnyAsync(resource => resource.Url == url && resource.Section == item.Domain));
        }
        Assert.True(await db.AppMetadataEntries.AnyAsync(entry => entry.Key == MetadataKey));
    }

    [Fact]
    public async Task Existing_plan_adds_missing_items_once_without_overwriting_progress_or_recreating_deletions()
    {
        await using var app = new PortalApplicationFactory();
        await using (var db = await app.OpenDatabaseAsync())
        {
            // Simulate a pre-upgrade database with one manually added Karpathy project.
            var items = await db.TrainingItems.Where(item => item.Category == "Andrej Karpathy")
                .OrderBy(item => item.TargetDate).ToListAsync();
            db.TrainingItems.RemoveRange(items.Skip(1));
            var existing = items[0];
            existing.Title = $" {existing.Title.ToUpperInvariant()} ";
            existing.Status = TrackerStatus.InProgress;
            existing.ProgressPercent = 45;
            existing.Notes = "My own learning notes";
            existing.Evidence = "Saved gradient comparison";
            existing.TargetDate = new DateTime(2028, 1, 1);
            db.AppMetadataEntries.Remove(await db.AppMetadataEntries.SingleAsync(entry => entry.Key == MetadataKey));
            await db.SaveChangesAsync();
        }

        await DatabaseInitializer.InitializeAsync(app.Services);
        await DatabaseInitializer.InitializeAsync(app.Services);

        await using (var db = await app.OpenDatabaseAsync())
        {
            var items = await db.TrainingItems.Where(item => item.Category == "Andrej Karpathy").ToListAsync();
            Assert.Equal(4, items.Count);
            var existing = Assert.Single(items, item => item.Title.Contains("MICROGRAD"));
            Assert.Equal(TrackerStatus.InProgress, existing.Status);
            Assert.Equal(45, existing.ProgressPercent);
            Assert.Equal("My own learning notes", existing.Notes);
            Assert.Equal("Saved gradient comparison", existing.Evidence);
            Assert.Equal(new DateTime(2028, 1, 1), existing.TargetDate);
            Assert.Equal(4, await db.Resources.CountAsync(resource => resource.Section == "LLM Foundations"));
            Assert.Equal(1, await db.AppMetadataEntries.CountAsync(entry => entry.Key == MetadataKey));
            db.TrainingItems.Remove(existing);
            await db.SaveChangesAsync();
        }

        await DatabaseInitializer.InitializeAsync(app.Services);
        await using var finalDb = await app.OpenDatabaseAsync();
        Assert.Equal(3, await finalDb.TrainingItems.CountAsync(item => item.Category == "Andrej Karpathy"));
    }
}
