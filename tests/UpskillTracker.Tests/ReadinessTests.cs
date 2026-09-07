using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using UpskillTracker.Data;
using UpskillTracker.Services;

namespace UpskillTracker.Tests;

public sealed class ReadinessTests
{
    [Fact]
    public async Task Health_is_anonymous_non_sensitive_and_checks_live_database_state()
    {
        await using var app = new PortalApplicationFactory();
        using var client = app.CreatePortalClient();
        app.Services.GetRequiredService<DatabaseAvailabilityState>().MarkUnavailable();

        using var healthy = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        Assert.Equal("no-store", healthy.Headers.CacheControl?.ToString());
        var version = await AssertContractAsync(healthy, "healthy");

        await using var db = await app.OpenDatabaseAsync();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var schema = connection.CreateCommand();
        schema.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'LearningActivities'";
        var createTableSql = Assert.IsType<string>(await schema.ExecuteScalarAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TABLE LearningActivities");
        app.Services.GetRequiredService<DatabaseAvailabilityState>().MarkAvailable();

        using var unhealthy = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unhealthy.StatusCode);
        Assert.Equal(version, await AssertContractAsync(unhealthy, "unhealthy"));
        Assert.DoesNotContain(app.ConnectionString, await unhealthy.Content.ReadAsStringAsync());
        Assert.DoesNotContain("SQLite", await unhealthy.Content.ReadAsStringAsync());

        await db.Database.ExecuteSqlRawAsync(createTableSql);
        using var recovered = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        await AssertContractAsync(recovered, "healthy");
    }

    [Fact]
    public async Task Readiness_propagates_caller_cancellation_and_bounds_slow_connections()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["AccessPin"] = PortalApplicationFactory.TestPin }).Build();
        var sessions = new PinSessionService(configuration, cache, TimeProvider.System);
        var factory = new CancellableDbFactory();
        var readiness = new ReadinessService(factory, sessions);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readiness.CheckAsync(cancelled.Token));

        using var inFlightCancellation = new CancellationTokenSource();
        var inFlightProbe = readiness.CheckAsync(inFlightCancellation.Token);
        inFlightCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlightProbe);

        var elapsed = Stopwatch.StartNew();
        var result = await readiness.CheckAsync(CancellationToken.None);
        Assert.Equal("unhealthy", result.Status);
        Assert.True(factory.CancellationObserved);
        Assert.True(elapsed.Elapsed < ReadinessService.ProbeTimeout.Add(TimeSpan.FromSeconds(3)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Postgres_connection_failures_including_ef_wrappers_report_unhealthy(bool wrapped)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["AccessPin"] = PortalApplicationFactory.TestPin }).Build();
        var sessions = new PinSessionService(configuration, cache, TimeProvider.System);
        Exception failure = new NpgsqlException("Local-test database detail", new TimeoutException("Local-test timeout"));
        if (wrapped)
        {
            failure = new InvalidOperationException("Local-test EF execution strategy wrapper", failure);
        }

        var readiness = new ReadinessService(new FaultingDbFactory(failure), sessions);
        var result = await readiness.CheckAsync(CancellationToken.None);
        Assert.Equal("unhealthy", result.Status);
        Assert.DoesNotContain("Local-test", JsonSerializer.Serialize(result));
    }

    private static async Task<string> AssertContractAsync(HttpResponseMessage response, string status)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(["commit", "status", "version"], root.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal(status, root.GetProperty("status").GetString());
        var version = root.GetProperty("version").GetString();
        var informationalVersion = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        Assert.Equal(informationalVersion, version);
        var commit = root.GetProperty("commit").GetString();
        Assert.True(commit == "unknown" || commit is { Length: 40 or 64 } && commit.All(char.IsAsciiHexDigit));
        if (commit != "unknown")
        {
            Assert.EndsWith($"+{commit}", version, StringComparison.OrdinalIgnoreCase);
        }
        return version!;
    }

    private sealed class CancellableDbFactory : IDbContextFactory<TrackerDbContext>
    {
        public bool CancellationObserved { get; private set; }
        public TrackerDbContext CreateDbContext() => throw new NotSupportedException("Only the async probe is supported.");

        public async Task<TrackerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The bounded probe did not cancel.");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }

    private sealed class FaultingDbFactory(Exception failure) : IDbContextFactory<TrackerDbContext>
    {
        public TrackerDbContext CreateDbContext() => throw failure;
    }
}
