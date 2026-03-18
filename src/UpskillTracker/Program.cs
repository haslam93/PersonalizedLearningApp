using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using GitHub.Copilot.SDK;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using Npgsql;
using UpskillTracker.Components;
using UpskillTracker.Data;
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
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<GitHubOAuthOptions>(builder.Configuration.GetSection(GitHubOAuthOptions.SectionName));
builder.Services.Configure<CopilotSdkOptions>(builder.Configuration.GetSection(CopilotSdkOptions.SectionName));
builder.Services.Configure<YouTubeOptions>(builder.Configuration.GetSection(YouTubeOptions.SectionName));
builder.Services.AddSingleton<TokenCredential>(tokenCredential);
ConfigureDataProtection(builder.Services, storageOptions, tokenCredential);
ConfigureDbContext(builder.Services, storageOptions, tokenCredential, builder.Environment);
builder.Services.AddScoped<TrackerService>();
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
});

authenticationBuilder.AddCookie(options =>
{
    options.LoginPath = "/auth/github/login";
    options.LogoutPath = "/auth/github/logout";
    options.SlidingExpiration = true;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
});

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

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/auth/github/login", async (HttpContext httpContext) =>
{
    var returnUrl = NormalizeReturnUrl(httpContext.Request.Query["returnUrl"]);
    var options = httpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<GitHubOAuthOptions>>().Value;

    if (!options.IsConfigured)
    {
        httpContext.Response.Redirect(returnUrl);
        return;
    }

    await httpContext.ChallengeAsync(GitHubOAuthOptions.AuthenticationScheme, new AuthenticationProperties
    {
        RedirectUri = $"/auth/github/complete?returnUrl={Uri.EscapeDataString(returnUrl)}"
    });
}).AllowAnonymous();

app.MapGet("/auth/github/complete", (string? returnUrl) => Results.LocalRedirect(NormalizeReturnUrl(returnUrl))).AllowAnonymous();

app.MapGet("/auth/github/logout", async (HttpContext httpContext, GitHubTokenStore tokenStore, CopilotChatService copilotChatService) =>
{
    var authSessionId = httpContext.User.FindFirstValue(CopilotAuthClaims.SessionId);
    if (!string.IsNullOrWhiteSpace(authSessionId))
    {
        await tokenStore.RemoveAsync(authSessionId);
        await copilotChatService.ReleaseUserSessionAsync(authSessionId);
    }

    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect(NormalizeReturnUrl(httpContext.Request.Query["returnUrl"]));
}).AllowAnonymous();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

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
    var dataProtectionBuilder = services.AddDataProtection()
        .SetApplicationName(string.IsNullOrWhiteSpace(storageOptions.DataProtectionApplicationName)
            ? "UpskillTracker"
            : storageOptions.DataProtectionApplicationName);

    if (string.IsNullOrWhiteSpace(storageOptions.KeyBlobUri))
    {
        return;
    }

    var blobClient = CreateAndEnsureKeyBlobClientAsync(storageOptions.KeyBlobUri, credential).GetAwaiter().GetResult();
    dataProtectionBuilder.PersistKeysToAzureBlobStorage(blobClient);
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
    connectionStringBuilder.TrustServerCertificate = false;

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

static async Task<BlobClient> CreateAndEnsureKeyBlobClientAsync(string keyBlobUri, TokenCredential credential)
{
    var uri = new Uri(keyBlobUri);
    var blobUri = new Azure.Storage.Blobs.BlobUriBuilder(uri);
    var serviceClient = new BlobServiceClient(new Uri($"{uri.Scheme}://{uri.Host}"), credential);
    var containerClient = serviceClient.GetBlobContainerClient(blobUri.BlobContainerName);
    await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);

    var blobClient = containerClient.GetBlobClient(blobUri.BlobName);
    if (!await blobClient.ExistsAsync())
    {
        await blobClient.UploadAsync(BinaryData.FromString("<?xml version=\"1.0\" encoding=\"utf-8\"?><repository />"));
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

static string NormalizeReturnUrl(string? returnUrl)
{
    if (string.IsNullOrWhiteSpace(returnUrl))
    {
        return "/";
    }

    if (Uri.TryCreate(returnUrl, UriKind.Absolute, out _))
    {
        return "/";
    }

    return returnUrl.StartsWith('/') ? returnUrl : $"/{returnUrl}";
}
