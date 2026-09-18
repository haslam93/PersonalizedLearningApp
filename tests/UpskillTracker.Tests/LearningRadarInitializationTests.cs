using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UpskillTracker.Data;
using UpskillTracker.Models;

namespace UpskillTracker.Tests;

public sealed class LearningRadarInitializationTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("ar-SA")]
    public async Task Radar_upgrade_preserves_calendar_dates_and_existing_work_in_any_culture(string culture)
    {
        await using var app = new PortalApplicationFactory();
        await VerifyRadarUpgradeAsync(app, culture);
    }

    [PostgresFact]
    public async Task Postgres_starts_and_imports_new_radar_items_after_the_initial_plan_refresh()
    {
        var connectionString = Assert.IsType<string>(
            Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionStringVariable));
        await using var app = new PortalApplicationFactory(postgresConnectionString: connectionString);
        await VerifyRadarUpgradeAsync(app, "en-US");
    }

    private static async Task VerifyRadarUpgradeAsync(PortalApplicationFactory app, string culture)
    {
        var environment = app.Services.GetRequiredService<IWebHostEnvironment>();
        var seedPath = Path.Combine(environment.ContentRootPath, "Data", "SeedData", "learning-radar.json");
        await using var seedFile = File.OpenRead(seedPath);
        using var document = await JsonDocument.ParseAsync(seedFile);
        var entries = document.RootElement.GetProperty("items");
        var entry = entries[entries.GetArrayLength() - 1];
        var title = Assert.IsType<string>(entry.GetProperty("title").GetString());
        var expectedDate = DateOnly.ParseExact(
            Assert.IsType<string>(entry.GetProperty("targetDate").GetString()),
            "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var personalItem = new TrainingItem
        {
            Title = "Local-only custom learning plan",
            Domain = "Testing",
            TargetDate = new DateTime(2028, 1, 3, 0, 0, 0, DateTimeKind.Utc),
            Status = TrackerStatus.InProgress,
            ProgressPercent = 45,
            Notes = "Keep my own notes",
            Evidence = "Keep my existing evidence"
        };
        int expectedCount;

        await using (var db = await app.OpenDatabaseAsync())
        {
            Assert.True(await db.AppMetadataEntries.AnyAsync(
                metadata => metadata.Key == "fabric-opentext-events-priorities-v2"));
            // Simulate an existing installation receiving a new radar catalog item.
            db.TrainingItems.Remove(await db.TrainingItems.SingleAsync(item => item.Title == title));
            db.TrainingItems.Add(personalItem);
            await db.SaveChangesAsync();
            expectedCount = await db.TrainingItems.CountAsync() + 1;
        }

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            await DatabaseInitializer.InitializeAsync(app.Services);
            await DatabaseInitializer.InitializeAsync(app.Services);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }

        await using (var db = await app.OpenDatabaseAsync())
        {
            var imported = await db.TrainingItems.SingleAsync(item => item.Title == title);
            Assert.Equal(expectedDate, imported.TargetDate);
            if (db.Database.IsNpgsql())
            {
                Assert.Equal(DateTimeKind.Utc, imported.TargetDate.Kind);
            }

            Assert.Equal(expectedCount, await db.TrainingItems.CountAsync());
            var preserved = await db.TrainingItems.SingleAsync(item => item.Id == personalItem.Id);
            Assert.Equal(personalItem.TargetDate, preserved.TargetDate);
            Assert.Equal(personalItem.Status, preserved.Status);
            Assert.Equal(personalItem.ProgressPercent, preserved.ProgressPercent);
            Assert.Equal(personalItem.Notes, preserved.Notes);
            Assert.Equal(personalItem.Evidence, preserved.Evidence);
        }

        using var client = app.CreatePortalClient();
        using var health = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public const string ConnectionStringVariable = "POSTGRES_TEST_CONNECTION_STRING";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionStringVariable)))
        {
            Skip = $"Set {ConnectionStringVariable} to run against a local disposable PostgreSQL server.";
        }
    }
}
