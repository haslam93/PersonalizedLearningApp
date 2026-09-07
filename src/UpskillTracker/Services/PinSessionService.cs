using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;

namespace UpskillTracker.Services;

public sealed class PinSessionService(IConfiguration configuration, IMemoryCache cache, TimeProvider clock)
{
    public const string Scheme = "PortalPin";
    public const string Policy = "PortalAccess";
    public const string CookieName = "__Host-UpskillTracker.Pin";
    public const string SessionClaim = "upskilltracker:pin-session";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    public bool IsConfigured => IsSixDigits(configuration["AccessPin"]);

    public AuthenticationTicket? CreateTicket(string? enteredPin)
    {
        var requiredPin = configuration["AccessPin"];
        if (!IsSixDigits(requiredPin) || !IsSixDigits(enteredPin) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(requiredPin!), Encoding.ASCII.GetBytes(enteredPin!)))
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var expires = now.Add(Lifetime);
        var sessionId = Guid.NewGuid().ToString("N");
        cache.Set(CacheKey(sessionId), new PinSession(expires, HashPin(requiredPin!)), Lifetime);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(SessionClaim, sessionId)], Scheme));
        return new AuthenticationTicket(principal, new AuthenticationProperties
        {
            IssuedUtc = now,
            ExpiresUtc = expires,
            IsPersistent = false,
            AllowRefresh = false
        }, Scheme);
    }

    public bool HasAccess(ClaimsPrincipal? principal)
    {
        var requiredPin = configuration["AccessPin"];
        var sessionId = GetSessionId(principal);
        if (!IsSixDigits(requiredPin) || sessionId is null ||
            !cache.TryGetValue<PinSession>(CacheKey(sessionId), out var session) || session is null)
        {
            return false;
        }

        if (session.ExpiresUtc <= clock.GetUtcNow() ||
            !CryptographicOperations.FixedTimeEquals(session.PinHash, HashPin(requiredPin!)))
        {
            cache.Remove(CacheKey(sessionId));
            return false;
        }

        return true;
    }

    public void Revoke(ClaimsPrincipal? principal)
    {
        if (GetSessionId(principal) is { } sessionId)
        {
            cache.Remove(CacheKey(sessionId));
        }
    }

    private static string? GetSessionId(ClaimsPrincipal? principal) =>
        principal?.Identities
            .FirstOrDefault(identity => identity.IsAuthenticated && identity.AuthenticationType == Scheme)
            ?.FindFirst(SessionClaim)?.Value;

    private static bool IsSixDigits(string? value) =>
        value is { Length: 6 } && value.All(character => character is >= '0' and <= '9');

    private static byte[] HashPin(string value) => SHA256.HashData(Encoding.ASCII.GetBytes(value));
    private static string CacheKey(string sessionId) => $"{Scheme}:{sessionId}";
    private sealed record PinSession(DateTimeOffset ExpiresUtc, byte[] PinHash);
}
