using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UpskillTracker.Services;

namespace UpskillTracker.Tests;

public sealed class PinAccessTests
{
    [Fact]
    public async Task Anonymous_page_and_legacy_browser_flag_do_not_grant_access()
    {
        await using var app = new PortalApplicationFactory();
        using var client = app.CreatePortalClient();
        client.DefaultRequestHeaders.Add("Cookie", "upskilltracker.pin-unlocked=True");

        using var response = await client.GetAsync("/?view=notes");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/auth/pin?returnUrl=", response.Headers.Location?.OriginalString);
        using var login = await client.GetAsync(response.Headers.Location);
        var html = await login.Content.ReadAsStringAsync();
        Assert.Contains("Enter your access PIN", html);
        Assert.DoesNotContain("sessionStorage", html);
        Assert.DoesNotContain("_framework/blazor.web.js", html);
        Assert.DoesNotContain("operating model", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/api/announcements/opened")]
    [InlineData("/auth/pin/logout")]
    [InlineData("/auth/github/login")]
    [InlineData("/auth/github/logout")]
    [InlineData("/_blazor/negotiate?negotiateVersion=1")]
    public async Task Anonymous_state_changing_requests_are_rejected(string path)
    {
        await using var app = new PortalApplicationFactory();
        using var client = app.CreatePortalClient();
        using var response = await client.PostAsJsonAsync(path, Announcement());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_requires_antiforgery_and_does_not_accept_json_or_a_forged_cookie()
    {
        await using var app = new PortalApplicationFactory();
        using var client = app.CreatePortalClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{PinSessionService.CookieName}=not-a-server-ticket");

        using var forged = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, forged.StatusCode);
        using var withoutToken = await client.PostAsync("/auth/pin/login", PinForm(PortalApplicationFactory.TestPin));
        Assert.Equal(HttpStatusCode.BadRequest, withoutToken.StatusCode);
        using var json = await client.PostAsJsonAsync("/auth/pin/login", new { pin = PortalApplicationFactory.TestPin });
        Assert.Equal(HttpStatusCode.BadRequest, json.StatusCode);
    }

    [Theory]
    [InlineData("000000")]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public async Task Invalid_pin_is_rejected_without_echoing_it(string pin)
    {
        await using var app = new PortalApplicationFactory();
        using var client = app.CreatePortalClient();
        var token = await GetTokenAsync(client);
        using var response = await client.PostAsync("/auth/pin/login", PinForm(pin, token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Incorrect PIN", await response.Content.ReadAsStringAsync());
        Assert.False(HasPortalCookie(response));
        using var stillLocked = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, stillLocked.StatusCode);
    }

    [Fact]
    public async Task Correct_pin_sets_a_protected_session_cookie_and_survives_reload()
    {
        await using var app = new PortalApplicationFactory();
        using var client = app.CreatePortalClient();
        using var response = await LoginAsync(client);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith($"{PinSessionService.CookieName}=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PortalApplicationFactory.TestPin, cookie);

        for (var reload = 0; reload < 2; reload++)
        {
            using var portal = await client.GetAsync("/?view=notes");
            Assert.Equal(HttpStatusCode.OK, portal.StatusCode);
            var html = await portal.Content.ReadAsStringAsync();
            Assert.Contains("portal-lock-form", html);
            Assert.Contains("Preparing your training hub", html);
            Assert.DoesNotContain("Search notes", html);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public async Task Missing_or_malformed_configuration_fails_closed(string? configuredPin)
    {
        await using var app = new PortalApplicationFactory(configuredPin);
        using var client = app.CreatePortalClient();
        var token = await GetTokenAsync(client);
        using var response = await client.PostAsync("/auth/pin/login", PinForm(PortalApplicationFactory.TestPin, token));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("PIN access is not configured", await response.Content.ReadAsStringAsync());
        Assert.False(HasPortalCookie(response));
        using var health = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
    }

    [Theory]
    [InlineData("/?view=notes", "/?view=notes")]
    [InlineData("/?view=plan#next", "/?view=plan#next")]
    [InlineData("https://example.invalid/escape", "/")]
    [InlineData("//example.invalid/escape", "/")]
    [InlineData("/\\example.invalid/escape", "/")]
    [InlineData("/%2fexample.invalid/escape", "/")]
    [InlineData("/%5cexample.invalid/escape", "/")]
    [InlineData("/%0d%0aLocation:https://example.invalid", "/")]
    [InlineData("notes", "/")]
    public async Task Login_uses_only_safe_local_redirects(string requested, string expected)
    {
        await using var app = new PortalApplicationFactory();
        using var client = app.CreatePortalClient();
        using var response = await LoginAsync(client, requested);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expected, response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Attempts_are_limited_across_posts_cookies_and_forwarded_addresses()
    {
        await using var app = new PortalApplicationFactory();
        for (var attempt = 0; attempt < 6; attempt++)
        {
            using var client = app.CreatePortalClient();
            client.DefaultRequestHeaders.Add("X-Forwarded-For", $"192.0.2.{attempt + 1}");
            var token = await GetTokenAsync(client);
            using var response = await client.PostAsync("/auth/pin/login", PinForm("000000", token));
            Assert.Equal(attempt < 5 ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests, response.StatusCode);
            if (attempt == 5)
            {
                Assert.NotNull(response.Headers.RetryAfter);
                Assert.Contains("Too many attempts", await response.Content.ReadAsStringAsync());
            }
        }
    }

    [Fact]
    public async Task Announcement_writes_need_both_the_pin_session_and_antiforgery()
    {
        await using var app = new PortalApplicationFactory();
        using var client = app.CreatePortalClient();
        await using var db = await app.OpenDatabaseAsync();
        var initialCount = await db.AnnouncementStates.CountAsync();

        using var anonymous = await client.PostAsJsonAsync("/api/announcements/opened", Announcement());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(initialCount, await db.AnnouncementStates.CountAsync());

        using var login = await LoginAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        using var noToken = await client.PostAsJsonAsync("/api/announcements/opened", Announcement());
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.Equal(initialCount, await db.AnnouncementStates.CountAsync());

        client.DefaultRequestHeaders.Add(PinAuthentication.AntiforgeryHeader,
            await GetTokenAsync(client, "/auth/github/logout"));
        using var opened = await client.PostAsJsonAsync("/api/announcements/opened", Announcement());
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Equal(initialCount + 1, await db.AnnouncementStates.CountAsync());
    }

    [Fact]
    public async Task Expiry_and_configuration_removal_reject_previously_valid_cookies()
    {
        await using var app = new PortalApplicationFactory();
        using var client = app.CreatePortalClient();
        using var login = await LoginAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        app.Clock.Advance(PinSessionService.Lifetime);
        using var expired = await client.PostAsJsonAsync("/api/announcements/opened", Announcement());
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        using var reload = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, reload.StatusCode);

        using var freshLogin = await LoginAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, freshLogin.StatusCode);
        app.Services.GetRequiredService<IConfiguration>()["AccessPin"] = "";
        using var removedConfiguration = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, removedConfiguration.StatusCode);
    }

    [Fact]
    public async Task Lock_requires_post_and_antiforgery_and_revokes_replayed_cookies()
    {
        await using var app = new PortalApplicationFactory();
        using var client = app.CreatePortalClient();
        using var login = await LoginAsync(client);
        var cookie = GetPortalCookie(login);

        using var getLogout = await client.GetAsync("/auth/pin/logout");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, getLogout.StatusCode);
        using var noToken = await client.PostAsync("/auth/pin/logout", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);

        var token = await GetTokenAsync(client, "/auth/github/logout");
        using var logout = await client.PostAsync("/auth/pin/logout", PinForm("", token));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/auth/pin", logout.Headers.Location?.OriginalString);
        using var replayClient = app.CreatePortalClient(handleCookies: false);
        replayClient.DefaultRequestHeaders.Add("Cookie", cookie);
        using var replay = await replayClient.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode);
    }

    [Fact]
    public async Task GitHub_identity_cannot_unlock_portal_and_callback_requires_pin()
    {
        await using var app = new PortalApplicationFactory(configureOAuth: true);
        using var client = app.CreatePortalClient();
        client.DefaultRequestHeaders.Add("Cookie", app.CreateGitHubCookie());

        using var page = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        using var write = await client.PostAsJsonAsync("/api/announcements/opened", Announcement());
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
        using var callback = await client.GetAsync("/signin-github?code=local-test-code&state=local-test-state");
        Assert.Equal(HttpStatusCode.Unauthorized, callback.StatusCode);
        using var complete = await client.GetAsync("/auth/github/complete?returnUrl=//example.invalid");
        Assert.Equal(HttpStatusCode.Redirect, complete.StatusCode);
        Assert.StartsWith("/auth/pin", complete.Headers.Location?.OriginalString);

        var schemes = app.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, schemes.DefaultSignInScheme);
        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, schemes.DefaultScheme);
    }

    [Fact]
    public async Task GitHub_login_is_a_pin_and_antiforgery_protected_challenge()
    {
        await using var app = new PortalApplicationFactory(configureOAuth: true);
        using var client = app.CreatePortalClient();
        using var login = await LoginAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        using var confirmation = await client.GetAsync("/auth/github/login?returnUrl=//example.invalid");
        Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
        Assert.DoesNotContain(confirmation.Headers, header =>
            header.Key == "Set-Cookie" && header.Value.Any(value => value.Contains(".Correlation.", StringComparison.Ordinal)));
        using var noToken = await client.PostAsync("/auth/github/login", PinForm(""));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);

        var token = ExtractToken(await confirmation.Content.ReadAsStringAsync());
        using var challenge = await client.PostAsync("/auth/github/login", PinForm("", token, "//example.invalid"));
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        Assert.Equal("github.com", challenge.Headers.Location?.Host);
        Assert.Equal("/login/oauth/authorize", challenge.Headers.Location?.AbsolutePath);
    }

    [Fact]
    public async Task GitHub_logout_preserves_pin_and_get_does_not_delete_the_token()
    {
        await using var app = new PortalApplicationFactory(configureOAuth: true);
        using var client = app.CreatePortalClient();
        const string sessionId = "local-test-github-session";
        var tokenStore = app.Services.GetRequiredService<GitHubTokenStore>();
        await tokenStore.StoreAsync(new GitHubTokenSession(sessionId, "local-test-not-a-github-token",
            "local-test-github-user", null, null, DateTimeOffset.UtcNow));
        client.DefaultRequestHeaders.Add("Cookie", app.CreateGitHubCookie(sessionId));
        using var login = await LoginAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        using var confirmation = await client.GetAsync("/auth/github/logout");
        Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
        Assert.NotNull(await tokenStore.GetAsync(sessionId));
        var token = ExtractToken(await confirmation.Content.ReadAsStringAsync());
        using var logout = await client.PostAsync("/auth/github/logout", PinForm("", token, "//example.invalid"));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/", logout.Headers.Location?.OriginalString);
        Assert.Null(await tokenStore.GetAsync(sessionId));

        using var stillUnlocked = await client.GetAsync("/auth/github/login");
        Assert.Equal(HttpStatusCode.OK, stillUnlocked.StatusCode);
    }

    [Fact]
    public async Task Cross_origin_blazor_transport_is_rejected_even_with_pin()
    {
        await using var app = new PortalApplicationFactory();
        using var client = app.CreatePortalClient();
        using var login = await LoginAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        client.DefaultRequestHeaders.Add("Origin", "https://example.invalid");
        using var response = await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", new StringContent(""));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        using var sameOrigin = await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", new StringContent(""));
        Assert.Equal(HttpStatusCode.OK, sameOrigin.StatusCode);
    }

    [Theory]
    [InlineData(false, "http")]
    [InlineData(true, "https")]
    public async Task Forwarded_scheme_from_non_loopback_is_only_trusted_on_app_service(bool appService, string scheme)
    {
        await using var app = new PortalApplicationFactory(appServiceProxy: appService);
        using var client = app.CreatePortalClient();
        client.BaseAddress = new Uri("http://localhost");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var response = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(scheme, Assert.Single(response.Headers.GetValues("X-Test-Observed-Scheme")));
    }

    [Fact]
    public async Task App_service_tls_termination_supports_secure_forms_oauth_and_blazor()
    {
        await using var app = new PortalApplicationFactory(configureOAuth: true, appServiceProxy: true);
        using var client = app.CreatePortalClient(handleCookies: false);
        client.BaseAddress = new Uri("http://localhost");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var page = await client.GetAsync("/auth/pin");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        // The public browser sends these secure cookies to HTTPS; only the proxy-to-worker hop is HTTP.
        var antiforgeryCookie = Assert.Single(page.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal)).Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", antiforgeryCookie);
        using var login = await client.PostAsync("/auth/pin/login",
            PinForm(PortalApplicationFactory.TestPin, ExtractToken(await page.Content.ReadAsStringAsync())));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Contains("secure", string.Join("", login.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", $"{antiforgeryCookie}; {GetPortalCookie(login)}");
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        using var negotiate = await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", new StringContent(""));
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);

        var token = await GetTokenAsync(client, "/auth/github/login");
        using var challenge = await client.PostAsync("/auth/github/login", PinForm("", token));
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        Assert.Contains("redirect_uri=https%3A%2F%2Flocalhost%2Fsignin-github", challenge.Headers.Location?.Query);
    }

    private static object Announcement() => new
    {
        url = "https://example.invalid/local-test-announcement",
        title = "Local test announcement",
        stream = "MicrosoftOfficial"
    };

    internal static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string returnUrl = "/")
    {
        var token = await GetTokenAsync(client);
        return await client.PostAsync("/auth/pin/login", PinForm(PortalApplicationFactory.TestPin, token, returnUrl));
    }

    private static async Task<string> GetTokenAsync(HttpClient client, string path = "/auth/pin")
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return ExtractToken(await response.Content.ReadAsStringAsync());
    }

    private static string ExtractToken(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "The native POST form must contain an antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static FormUrlEncodedContent PinForm(string pin, string? token = null, string returnUrl = "/")
    {
        var fields = new Dictionary<string, string> { ["pin"] = pin, ["returnUrl"] = returnUrl };
        if (token is not null)
        {
            fields["__RequestVerificationToken"] = token;
        }

        return new FormUrlEncodedContent(fields);
    }

    private static bool HasPortalCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies) &&
        cookies.Any(cookie => cookie.StartsWith($"{PinSessionService.CookieName}=", StringComparison.Ordinal));

    private static string GetPortalCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie")
            .Single(cookie => cookie.StartsWith($"{PinSessionService.CookieName}=", StringComparison.Ordinal)).Split(';')[0];
}
