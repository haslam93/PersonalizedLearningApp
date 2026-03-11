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

        var completedItems = trainingItems.Count(item => item.Status == TrackerStatus.Completed);
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
            UpcomingItems = upcomingItems,
            FocusItems = focusItems,
            PinnedResources = pinnedResources,
            RecentNotes = recentNotes
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

    public async Task SaveTrainingItemAsync(TrainingItem item)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;

        if (item.Id == 0)
        {
            item.CreatedUtc = now;
            item.UpdatedUtc = now;
            db.TrainingItems.Add(item);
        }
        else
        {
            var existing = await db.TrainingItems.FirstAsync(existingItem => existingItem.Id == item.Id);
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
            existing.UpdatedUtc = now;
        }

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
}
