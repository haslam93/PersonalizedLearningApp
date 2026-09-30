using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using GitHub.Copilot.SDK;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using Npgsql;
using UpskillTracker.Components;
using UpskillTracker.Components.Authentication;
using UpskillTracker.Data;
using UpskillTracker.Models;
using UpskillTracker.Services;

var builder = WebApplication.CreateBuilder(args);
var storageOptions = builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new();
var gitHubOAuthOptions = builder.Configuration.GetSection(GitHubOAuthOptions.SectionName).Get<GitHubOAuthOptions>() ?? new();
var tokenCredential = CreateTokenCredential(storageOptions);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddMemoryCache();
builder.Services.AddMudServices();
builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<DatabaseAvailabilityState>();
builder.Services.AddScoped<ReadinessService>();
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<GitHubOAuthOptions>(builder.Configuration.GetSection(GitHubOAuthOptions.SectionName));
builder.Services.Configure<CopilotSdkOptions>(builder.Configuration.GetSection(CopilotSdkOptions.SectionName));
builder.Services.Configure<YouTubeOptions>(builder.Configuration.GetSection(YouTubeOptions.SectionName));
builder.Services.AddSingleton<TokenCredential>(tokenCredential);
ConfigureDataProtection(builder.Services, storageOptions, tokenCredential);
ConfigureDbContext(builder.Services, storageOptions, tokenCredential, builder.Environment);
builder.Services.AddScoped<TrackerService>();
builder.Services.AddScoped<BrowserTimeZoneService>();
builder.Services.AddScoped<CopilotAuthService>();
builder.Services.AddSingleton<GitHubTokenStore>();
builder.Services.AddSingleton<CopilotChatService>();
builder.Services.AddHttpClient<AnnouncementFeedService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("UpskillTracker/1.0");
});
builder.Services.AddHttpClient<YouTubeVideoService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("UpskillTracker/1.0");
});

var authenticationBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = PinSessionService.Scheme;
    options.DefaultForbidScheme = PinSessionService.Scheme;
});

authenticationBuilder.AddCookie(options =>
{
    options.LoginPath = "/auth/github/login";
    options.LogoutPath = "/auth/github/logout";
    options.SlidingExpiration = true;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
});
authenticationBuilder.AddPortalPin(builder.Environment);

if (gitHubOAuthOptions.IsConfigured)
{
    authenticationBuilder.AddOAuth(GitHubOAuthOptions.AuthenticationScheme, options =>
    {
        options.ClientId = gitHubOAuthOptions.ClientId;
        options.ClientSecret = gitHubOAuthOptions.ClientSecret;
        options.CallbackPath = gitHubOAuthOptions.CallbackPath;
        options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
        options.TokenEndpoint = "https://github.com/login/oauth/access_token";
        options.UserInformationEndpoint = "https://api.github.com/user";
        options.SaveTokens = false;

        options.Scope.Clear();
        foreach (var scope in gitHubOAuthOptions.Scopes)
        {
            options.Scope.Add(scope);
        }

        options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
        options.ClaimActions.MapJsonKey(CopilotAuthClaims.GitHubLogin, "login");
        options.ClaimActions.MapJsonKey(ClaimTypes.Name, "name");
        options.ClaimActions.MapJsonKey(CopilotAuthClaims.AvatarUrl, "avatar_url");

        options.Events = new OAuthEvents
        {
            OnCreatingTicket = async context =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                request.Headers.UserAgent.ParseAdd("UpskillTracker/1.0");

                using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
                response.EnsureSuccessStatusCode();

                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
                context.RunClaimActions(payload.RootElement);

                var login = payload.RootElement.TryGetProperty("login", out var loginProperty)
                    ? loginProperty.GetString() ?? string.Empty
                    : string.Empty;
                var displayName = payload.RootElement.TryGetProperty("name", out var nameProperty)
                    ? nameProperty.GetString()
                    : null;
                var avatarUrl = payload.RootElement.TryGetProperty("avatar_url", out var avatarProperty)
                    ? avatarProperty.GetString()
                    : null;

                var authSessionId = Guid.NewGuid().ToString("N");
                var tokenStore = context.HttpContext.RequestServices.GetRequiredService<GitHubTokenStore>();
                await tokenStore.StoreAsync(new GitHubTokenSession(
                    authSessionId,
                    context.AccessToken ?? throw new InvalidOperationException("GitHub OAuth did not return an access token."),
                    login,
                    displayName,
                    avatarUrl,
                    DateTimeOffset.UtcNow));

                context.Identity?.AddClaim(new Claim(CopilotAuthClaims.SessionId, authSessionId));

                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    context.Identity?.AddClaim(new Claim(ClaimTypes.Name, displayName));
                }
                else if (!string.IsNullOrWhiteSpace(login))
                {
                    context.Identity?.AddClaim(new Claim(ClaimTypes.Name, login));
                }

                if (!string.IsNullOrWhiteSpace(login))
                {
                    context.Identity?.AddClaim(new Claim(CopilotAuthClaims.GitHubLogin, login));
                }

                if (!string.IsNullOrWhiteSpace(avatarUrl))
                {
                    context.Identity?.AddClaim(new Claim(CopilotAuthClaims.AvatarUrl, avatarUrl));
                }
            }
        };
    });
}

var app = builder.Build();

await DatabaseInitializer.InitializeAsync(app.Services);

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UsePortalPinAuthentication(gitHubOAuthOptions.CallbackPath);
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapPortalPinEndpoints();

app.MapGet("/healthz", async (HttpContext context, ReadinessService readiness) =>
{
    var result = await readiness.CheckAsync(context.RequestAborted);
    return Results.Json(result, statusCode: result.Status == "healthy"
        ? StatusCodes.Status200OK
        : StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();

app.MapGet("/auth/github/login", (string? returnUrl) =>
    new RazorComponentResult<AuthenticationPage>(new
    {
        Action = "github-login",
        ReturnUrl = LocalReturnUrl.Normalize(returnUrl)
    })).RequireAuthorization(PinSessionService.Policy);

app.MapPost("/auth/github/login", async (HttpContext httpContext) =>
{
    var form = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
    var returnUrl = LocalReturnUrl.Normalize(form["returnUrl"]);
    var options = httpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<GitHubOAuthOptions>>().Value;

    if (!options.IsConfigured)
    {
        return Results.LocalRedirect(returnUrl);
    }

    return Results.Challenge(new AuthenticationProperties
        {
            RedirectUri = $"/auth/github/complete?returnUrl={Uri.EscapeDataString(returnUrl)}"
        },
        [GitHubOAuthOptions.AuthenticationScheme]);
}).RequireAuthorization(PinSessionService.Policy)
    .AddEndpointFilter<PortalAntiforgeryFilter>();

app.MapGet("/auth/github/complete", (string? returnUrl) => Results.LocalRedirect(LocalReturnUrl.Normalize(returnUrl)))
    .RequireAuthorization(PinSessionService.Policy);

app.MapGet("/auth/github/logout", (string? returnUrl) =>
    new RazorComponentResult<AuthenticationPage>(new
    {
        Action = "github-logout",
        ReturnUrl = LocalReturnUrl.Normalize(returnUrl)
    })).RequireAuthorization(PinSessionService.Policy);

app.MapPost("/auth/github/logout", async (HttpContext httpContext, GitHubTokenStore tokenStore, CopilotChatService copilotChatService) =>
{
    var authSessionId = httpContext.User.FindFirstValue(CopilotAuthClaims.SessionId);
    if (!string.IsNullOrWhiteSpace(authSessionId))
    {
        await tokenStore.RemoveAsync(authSessionId);
        await copilotChatService.ReleaseUserSessionAsync(authSessionId);
    }

    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    var form = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
    return Results.LocalRedirect(LocalReturnUrl.Normalize(form["returnUrl"]));
}).RequireAuthorization(PinSessionService.Policy)
    .AddEndpointFilter<PortalAntiforgeryFilter>();

app.MapPost("/api/announcements/opened", async (AnnouncementOpenRequest request, TrackerService trackerService) =>
{
    if (string.IsNullOrWhiteSpace(request.Url))
    {
        return Results.BadRequest();
    }

    var announcement = new AnnouncementItem
    {
        Url = request.Url,
        Title = string.IsNullOrWhiteSpace(request.Title) ? request.Url : request.Title,
        Summary = request.Summary ?? string.Empty,
        PublishedUtc = NormalizeAnnouncementPublishedUtc(request.PublishedUtc),
        Source = string.IsNullOrWhiteSpace(request.Source) ? "Unknown" : request.Source,
        Topic = string.IsNullOrWhiteSpace(request.Topic) ? "General" : request.Topic,
        Stream = ParseAnnouncementStream(request.Stream),
        SourceUrl = request.SourceUrl ?? string.Empty
    };

    try
    {
        await trackerService.MarkAnnouncementOpenedAsync(announcement);
    }
    catch (TrackerStorageUnavailableException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok();
}).RequireAuthorization(PinSessionService.Policy)
    .AddEndpointFilter<PortalAntiforgeryFilter>();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization(PinSessionService.Policy);

app.Run();

static void ConfigureDbContext(IServiceCollection services, StorageOptions storageOptions, TokenCredential credential, IWebHostEnvironment environment)
{
    if (IsPostgresStorage(storageOptions))
    {
        var dataSource = CreatePostgresDataSource(storageOptions, credential);
        services.AddSingleton(dataSource);
        services.AddDbContextFactory<TrackerDbContext>(options => options.UseNpgsql(dataSource));
        return;
    }

    var sqliteConnectionString = ResolveSqliteConnectionString(storageOptions.ConnectionString, environment);
    services.AddDbContextFactory<TrackerDbContext>(options => options.UseSqlite(sqliteConnectionString));
}

static void ConfigureDataProtection(IServiceCollection services, StorageOptions storageOptions, TokenCredential credential)
{
    var dataProtectionBlobStartupTimeout = TimeSpan.FromSeconds(20);
    var dataProtectionBuilder = services.AddDataProtection()
        .SetApplicationName(string.IsNullOrWhiteSpace(storageOptions.DataProtectionApplicationName)
            ? "UpskillTracker"
            : storageOptions.DataProtectionApplicationName);

    if (string.IsNullOrWhiteSpace(storageOptions.KeyBlobUri))
    {
        return;
    }

    try
    {
        using var startupTimeout = new CancellationTokenSource(dataProtectionBlobStartupTimeout);
        var blobClient = CreateAndEnsureKeyBlobClientAsync(storageOptions.KeyBlobUri, credential, startupTimeout.Token)
            .GetAwaiter().GetResult();
        dataProtectionBuilder.PersistKeysToAzureBlobStorage(blobClient);
    }
    catch (Exception exception) when (exception is OperationCanceledException or RequestFailedException or AuthenticationFailedException)
    {
        // Persisting keys to blob storage is best-effort at startup: a slow or unavailable dependency here
        // (e.g. transient managed-identity token acquisition delays) must not prevent Kestrel from binding
        // and serving traffic. Fall back to ephemeral, process-local keys so the app can still start.
        // Operational tradeoff: ephemeral keys do not survive process restarts, so any protected payloads
        // (auth cookies, antiforgery tokens, etc.) issued before the restart become invalid afterwards.
        // A standalone bootstrap logger is used (rather than building the app's service provider here) to
        // avoid creating a second copy of singleton services this early in startup; it still writes to the
        // console, which App Service captures the same way as the rest of the app's startup diagnostics.
        using var bootstrapLoggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole());
        bootstrapLoggerFactory.CreateLogger("DataProtectionStartup").LogCritical(
            exception,
            "Could not reach Azure Blob Storage for data protection keys within {Timeout}. " +
            "Continuing with ephemeral, process-local keys; previously issued protected payloads will be invalidated.",
            dataProtectionBlobStartupTimeout);
    }
}

static TokenCredential CreateTokenCredential(StorageOptions storageOptions)
{
    var options = new DefaultAzureCredentialOptions();
    if (!string.IsNullOrWhiteSpace(storageOptions.ManagedIdentityClientId))
    {
        options.ManagedIdentityClientId = storageOptions.ManagedIdentityClientId;
    }

    return new DefaultAzureCredential(options);
}

static NpgsqlDataSource CreatePostgresDataSource(StorageOptions storageOptions, TokenCredential credential)
{
    var connectionStringBuilder = new NpgsqlConnectionStringBuilder(storageOptions.ConnectionString);
    if (!string.IsNullOrWhiteSpace(storageOptions.DatabaseUser) && string.IsNullOrWhiteSpace(connectionStringBuilder.Username))
    {
        connectionStringBuilder.Username = storageOptions.DatabaseUser;
    }

    connectionStringBuilder.SslMode = SslMode.Require;

    if (string.IsNullOrWhiteSpace(connectionStringBuilder.Username))
    {
        throw new InvalidOperationException("Storage:DatabaseUser or Username in the PostgreSQL connection string is required.");
    }

    if (!storageOptions.UseManagedIdentity)
    {
        return NpgsqlDataSource.Create(connectionStringBuilder.ConnectionString);
    }

    var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionStringBuilder.ConnectionString);
    dataSourceBuilder.UsePeriodicPasswordProvider(
        async (_, cancellationToken) =>
        {
            var accessToken = await credential.GetTokenAsync(
                new TokenRequestContext(["https://ossrdbms-aad.database.windows.net/.default"]),
                cancellationToken);
            return accessToken.Token;
        },
        TimeSpan.FromMinutes(55),
        TimeSpan.FromSeconds(30));

    return dataSourceBuilder.Build();
}

static async Task<BlobClient> CreateAndEnsureKeyBlobClientAsync(string keyBlobUri, TokenCredential credential, CancellationToken cancellationToken)
{
    var uri = new Uri(keyBlobUri);
    var blobUri = new Azure.Storage.Blobs.BlobUriBuilder(uri);
    var serviceClient = new BlobServiceClient(new Uri($"{uri.Scheme}://{uri.Host}"), credential);
    var containerClient = serviceClient.GetBlobContainerClient(blobUri.BlobContainerName);
    await containerClient.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);

    var blobClient = containerClient.GetBlobClient(blobUri.BlobName);
    if (!await blobClient.ExistsAsync(cancellationToken))
    {
        await blobClient.UploadAsync(
            BinaryData.FromString("<?xml version=\"1.0\" encoding=\"utf-8\"?><repository />"),
            cancellationToken: cancellationToken);
    }

    return blobClient;
}

static bool IsPostgresStorage(StorageOptions storageOptions)
    => storageOptions.Provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase)
        || storageOptions.Provider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase)
        || storageOptions.ConnectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase);

static string ResolveSqliteConnectionString(string configuredConnectionString, IWebHostEnvironment environment)
{
    var connectionString = string.IsNullOrWhiteSpace(configuredConnectionString)
        ? $"Data Source={Path.Combine(environment.ContentRootPath, "Data", "upskilltracker.db")}"
        : configuredConnectionString.Replace("%CONTENTROOT%", environment.ContentRootPath, StringComparison.OrdinalIgnoreCase);

    var dataSourceSegment = connectionString
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault(segment => segment.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase));

    var dataSource = dataSourceSegment?.Split('=', 2)[1];
    if (!string.IsNullOrWhiteSpace(dataSource))
    {
        var directory = Path.GetDirectoryName(dataSource);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    return connectionString;
}

static DateTime NormalizeAnnouncementPublishedUtc(DateTime? publishedUtc)
{
    if (publishedUtc is null)
    {
        return DateTime.UtcNow;
    }

    return publishedUtc.Value.Kind switch
    {
        DateTimeKind.Utc => publishedUtc.Value,
        DateTimeKind.Local => publishedUtc.Value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(publishedUtc.Value, DateTimeKind.Utc)
    };
}

static AnnouncementStream ParseAnnouncementStream(string? stream)
    => Enum.TryParse<AnnouncementStream>(stream, ignoreCase: true, out var parsed)
        ? parsed
        : AnnouncementStream.MicrosoftOfficial;

internal sealed record AnnouncementOpenRequest(
    string Url,
    string? Title,
    string? Summary,
    DateTime? PublishedUtc,
    string? Source,
    string? Topic,
    string? Stream,
    string? SourceUrl);

public partial class Program;
