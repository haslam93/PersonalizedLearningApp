using System.ComponentModel;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using GitHub.Copilot.SDK;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using MudBlazor.Services;
using UpskillTracker.Components;
using UpskillTracker.Data;
using UpskillTracker.Services;

var builder = WebApplication.CreateBuilder(args);
var connectionString = ResolveSqliteConnectionString(builder.Configuration, builder.Environment);
var gitHubOAuthOptions = builder.Configuration.GetSection(GitHubOAuthOptions.SectionName).Get<GitHubOAuthOptions>() ?? new();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddMemoryCache();
builder.Services.AddMudServices();
builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddHttpContextAccessor();
builder.Services.Configure<GitHubOAuthOptions>(builder.Configuration.GetSection(GitHubOAuthOptions.SectionName));
builder.Services.Configure<CopilotSdkOptions>(builder.Configuration.GetSection(CopilotSdkOptions.SectionName));
builder.Services.AddDbContextFactory<TrackerDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddScoped<TrackerService>();
builder.Services.AddScoped<CopilotAuthService>();
builder.Services.AddSingleton<GitHubTokenStore>();
builder.Services.AddSingleton<CopilotChatService>();
builder.Services.AddHttpClient<AnnouncementFeedService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
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
                tokenStore.Store(new GitHubTokenSession(
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

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
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
        tokenStore.Remove(authSessionId);
        await copilotChatService.ReleaseUserSessionAsync(authSessionId);
    }

    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect(NormalizeReturnUrl(httpContext.Request.Query["returnUrl"]));
}).AllowAnonymous();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static string ResolveSqliteConnectionString(IConfiguration configuration, IWebHostEnvironment environment)
{
    var configured = configuration["Storage:ConnectionString"];
    var connectionString = string.IsNullOrWhiteSpace(configured)
        ? $"Data Source={Path.Combine(environment.ContentRootPath, "Data", "upskilltracker.db")}"
        : configured.Replace("%CONTENTROOT%", environment.ContentRootPath, StringComparison.OrdinalIgnoreCase);

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
