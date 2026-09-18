using System.Net;
using System.Security.Claims;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using UpskillTracker.Data;
using UpskillTracker.Services;

namespace UpskillTracker.Tests;

internal sealed class PortalApplicationFactory : WebApplicationFactory<Program>
{
    // Deliberately public, local-only test data. Never use a real portal PIN or GitHub credential here.
    public const string TestPin = "123456";
    private readonly string directory = Directory.CreateTempSubdirectory("upskill-pin-tests-").FullName;
    private readonly Dictionary<string, string?> configuration;
    private readonly bool usePostgres;

    public AdjustableTimeProvider Clock { get; } = new();
    public string ConnectionString { get; }

    public PortalApplicationFactory(
        string? pin = TestPin,
        bool configureOAuth = false,
        bool appServiceProxy = false,
        string? postgresConnectionString = null)
    {
        usePostgres = postgresConnectionString is not null;
        if (usePostgres)
        {
            var connection = new NpgsqlConnectionStringBuilder(postgresConnectionString);
            if (connection.Host is not ("localhost" or "127.0.0.1" or "::1"))
            {
                throw new ArgumentException("PostgreSQL tests must use a local disposable server.", nameof(postgresConnectionString));
            }

            connection.Database = $"upskill_tests_{Guid.NewGuid():N}";
            connection.Pooling = false;
            ConnectionString = connection.ConnectionString;
        }
        else
        {
            ConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "local-test.db")
            }.ToString();
        }

        configuration = new Dictionary<string, string?>
        {
            ["environment"] = "Testing",
            ["WEBSITE_INSTANCE_ID"] = appServiceProxy ? "local-test-app-service-instance" : "",
            ["AccessPin"] = pin ?? "",
            ["Storage:Provider"] = usePostgres ? "Postgres" : "Sqlite",
            ["Storage:ConnectionString"] = ConnectionString,
            ["Storage:EnableLegacySqliteImport"] = "false",
            ["Storage:UseManagedIdentity"] = "false",
            ["Storage:KeyBlobUri"] = "",
            ["GitHubOAuth:ClientId"] = configureOAuth ? "local-test-client-id" : "",
            ["GitHubOAuth:ClientSecret"] = configureOAuth ? "local-test-not-a-secret" : "",
            ["YouTube:ApiKey"] = "",
            ["ApplicationInsights:ConnectionString"] = "",
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = ""
        };
    }

    public HttpClient CreatePortalClient(bool handleCookies = true) => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = handleCookies
    });

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Host configuration is available before top-level Program reads Storage and OAuth options.
        builder.ConfigureHostConfiguration(settings => settings.AddInMemoryCollection(configuration));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, settings) => settings.AddInMemoryCollection(configuration));
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDbContextFactory<TrackerDbContext>>();
            services.AddSingleton<IDbContextFactory<TrackerDbContext>>(new IsolatedDbFactory(ConnectionString, usePostgres));
            services.RemoveAll<IDataProtectionProvider>();
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IStartupFilter, TestRemoteAddressFilter>();
            services.Configure<TelemetryConfiguration>(options => options.DisableTelemetry = true);
            services.ConfigureAll<HttpClientFactoryOptions>(options =>
                options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = new OfflineHandler()));
        });
    }

    public string CreateGitHubCookie(string? sessionId = null)
    {
        var options = Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "local-test-github-user-id"),
            new(ClaimTypes.Name, "Local Test GitHub User"),
            new(CopilotAuthClaims.GitHubLogin, "local-test-github-user")
        };
        if (sessionId is not null)
        {
            claims.Add(new Claim(CopilotAuthClaims.SessionId, sessionId));
        }

        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties
            {
                IssuedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1)
            }, CookieAuthenticationDefaults.AuthenticationScheme);
        return $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}";
    }

    public async Task<TrackerDbContext> OpenDatabaseAsync() =>
        await Services.GetRequiredService<IDbContextFactory<TrackerDbContext>>().CreateDbContextAsync();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (usePostgres)
        {
            await using var db = new IsolatedDbFactory(ConnectionString, usePostgres).CreateDbContext();
            await db.Database.EnsureDeletedAsync();
        }
        else
        {
            using var connection = new SqliteConnection(ConnectionString);
            SqliteConnection.ClearPool(connection);
        }

        Directory.Delete(directory, recursive: true);
    }

    private sealed class IsolatedDbFactory(string connectionString, bool usePostgres) : IDbContextFactory<TrackerDbContext>
    {
        public TrackerDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<TrackerDbContext>();
            if (usePostgres)
            {
                options.UseNpgsql(connectionString);
            }
            else
            {
                options.UseSqlite(connectionString);
            }

            return new TrackerDbContext(options.Options);
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private sealed class TestRemoteAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
        {
            application.Use((context, continuation) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.100");
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers["X-Test-Observed-Scheme"] = context.Request.Scheme;
                    return Task.CompletedTask;
                });
                return continuation(context);
            });
            next(application);
        };
    }
}

internal sealed class AdjustableTimeProvider : TimeProvider
{
    private long utcTicks = DateTimeOffset.UtcNow.UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);
    public void Advance(TimeSpan duration) => Interlocked.Add(ref utcTicks, duration.Ticks);
}
