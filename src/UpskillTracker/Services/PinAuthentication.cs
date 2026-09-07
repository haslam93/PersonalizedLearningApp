using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using UpskillTracker.Components.Authentication;

namespace UpskillTracker.Services;

public static class PinAuthentication
{
    public const string LoginPath = "/auth/pin";
    public const string AntiforgeryHeader = "X-CSRF-TOKEN";
    public const string LoginRateLimit = "pin-login";

    public static void AddPortalPin(
        this AuthenticationBuilder authentication, IWebHostEnvironment environment)
    {
        var services = authentication.Services;
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<PinSessionService>();
        services.AddSingleton<IAuthorizationHandler, PinAccessAuthorizationHandler>();
        services.AddScoped<AuthenticationStateProvider, PinCircuitAuthenticationStateProvider>();
        services.AddScoped<CircuitHandler, PinCircuitHandler>();
        services.AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
            if (!string.IsNullOrWhiteSpace(configuration["WEBSITE_INSTANCE_ID"]))
            {
                // App Service controls worker ingress. Trust its scheme, never forwarded host or client IP.
                options.KnownNetworks.Clear();
                options.KnownProxies.Clear();
            }
        });
        services.AddAuthorization(options =>
        {
            var policy = new AuthorizationPolicyBuilder().AddRequirements(new PinAccessRequirement()).Build();
            options.AddPolicy(PinSessionService.Policy, policy);
            options.DefaultPolicy = policy;
            options.FallbackPolicy = policy;
        });
        services.AddAntiforgery(options =>
        {
            options.HeaderName = AntiforgeryHeader;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
        });
        services.AddRateLimiter(options =>
        {
            // A shared PIN needs a shared attempt budget, not one that can be reset by changing cookies or IPs.
            options.AddFixedWindowLimiter(LoginRateLimit, limiter =>
            {
                limiter.PermitLimit = 5;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
            });
            options.OnRejected = async (context, cancellationToken) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay)
                    ? Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds))
                    : 60;
                context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
                cancellationToken.ThrowIfCancellationRequested();
                await Page("rate-limit", StatusCodes.Status429TooManyRequests).ExecuteAsync(context.HttpContext);
            };
        });
        authentication.AddCookie(PinSessionService.Scheme, options =>
        {
            options.Cookie.Name = environment.IsDevelopment() ? "UpskillTracker.Pin" : PinSessionService.CookieName;
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
            options.LoginPath = LoginPath;
            options.ExpireTimeSpan = PinSessionService.Lifetime;
            options.SlidingExpiration = false;
            options.Events.OnValidatePrincipal = context =>
            {
                if (!context.HttpContext.RequestServices.GetRequiredService<PinSessionService>().HasAccess(context.Principal))
                {
                    context.RejectPrincipal();
                }

                return Task.CompletedTask;
            };
            options.Events.OnRedirectToLogin = RedirectToPin;
            options.Events.OnRedirectToAccessDenied = RedirectToPin;
        });
        services.AddOptions<CookieAuthenticationOptions>(PinSessionService.Scheme)
            .Configure<TimeProvider>((options, clock) => options.TimeProvider = clock);
    }

    public static void UsePortalPinAuthentication(this WebApplication app, string gitHubCallbackPath)
    {
        app.Use(async (context, next) =>
        {
            // OAuth callbacks are handled inside UseAuthentication, before endpoint authorization.
            if (context.Request.Path.Equals(new PathString(gitHubCallbackPath)) &&
                !(await context.AuthenticateAsync(PinSessionService.Scheme)).Succeeded)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Unlock the portal before connecting GitHub.", context.RequestAborted);
                return;
            }

            await next(context);
        });
        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            var pin = await context.AuthenticateAsync(PinSessionService.Scheme);
            if (pin.Principal is not null && pin.Succeeded)
            {
                // Keep GitHub (or the anonymous identity) primary so PIN access isn't Copilot sign-in.
                context.User.AddIdentities(pin.Principal.Identities);
            }

            if (context.Request.Path.StartsWithSegments("/_blazor") &&
                context.Request.Headers.Origin is { Count: > 0 } origins &&
                !string.Equals(origins.ToString(), $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (!context.Request.Path.StartsWithSegments("/_framework"))
            {
                context.Response.Headers.CacheControl = "no-store";
            }

            await next(context);
        });
    }

    public static void MapPortalPinEndpoints(this WebApplication app)
    {
        app.MapGet(LoginPath, (HttpContext context, PinSessionService sessions) =>
        {
            var returnUrl = LocalReturnUrl.Normalize(context.Request.Query["returnUrl"]);
            return sessions.HasAccess(context.User)
                ? (IResult)Results.LocalRedirect(returnUrl)
                : Page(context.Request.Query["error"], StatusCodes.Status200OK, returnUrl);
        }).AllowAnonymous();

        app.MapPost("/auth/pin/login", async (HttpContext context, PinSessionService sessions) =>
        {
            if (!context.Request.HasFormContentType)
            {
                return Results.BadRequest();
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var returnUrl = LocalReturnUrl.Normalize(form["returnUrl"]);
            if (!sessions.IsConfigured)
            {
                return Page("configuration", StatusCodes.Status503ServiceUnavailable, returnUrl);
            }

            var ticket = sessions.CreateTicket(form["pin"]);
            if (ticket is null)
            {
                return Page("incorrect", StatusCodes.Status401Unauthorized, returnUrl);
            }

            sessions.Revoke(context.User);
            await context.SignInAsync(PinSessionService.Scheme, ticket.Principal, ticket.Properties);
            return Results.LocalRedirect(returnUrl);
        }).AllowAnonymous()
            .RequireRateLimiting(LoginRateLimit)
            .AddEndpointFilter<PortalAntiforgeryFilter>();

        app.MapPost("/auth/pin/logout", async (HttpContext context, PinSessionService sessions) =>
        {
            sessions.Revoke(context.User);
            await context.SignOutAsync(PinSessionService.Scheme);
            return Results.LocalRedirect(LoginPath);
        }).RequireAuthorization(PinSessionService.Policy)
            .AddEndpointFilter<PortalAntiforgeryFilter>();
    }

    private static RazorComponentResult<AuthenticationPage> Page(
        string? errorCode, int statusCode, string returnUrl = "/") =>
        new(new { ErrorCode = errorCode, ReturnUrl = returnUrl }) { StatusCode = statusCode };

    private static Task RedirectToPin(RedirectContext<CookieAuthenticationOptions> context)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) ||
            request.Path.StartsWithSegments("/api") ||
            request.Path.StartsWithSegments("/_blazor"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        }
        else
        {
            var returnUrl = LocalReturnUrl.Normalize($"{request.PathBase}{request.Path}{request.QueryString}");
            context.Response.Redirect($"{LoginPath}?returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        return Task.CompletedTask;
    }
}
