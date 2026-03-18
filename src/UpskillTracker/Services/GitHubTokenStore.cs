using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using UpskillTracker.Data;
using UpskillTracker.Models;

namespace UpskillTracker.Services;

public sealed class GitHubTokenStore(IDbContextFactory<TrackerDbContext> dbFactory, IDataProtectionProvider dataProtectionProvider)
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("GitHubTokenStore.AccessToken");

    public async Task StoreAsync(GitHubTokenSession session, TimeSpan? lifetime = null)
    {
        var now = DateTime.UtcNow;
        var expiresUtc = now.Add(lifetime ?? TimeSpan.FromHours(8));

        await using var db = await dbFactory.CreateDbContextAsync();
        var existing = await db.GitHubAuthSessions.FirstOrDefaultAsync(current => current.SessionId == session.SessionId);
        if (existing is null)
        {
            db.GitHubAuthSessions.Add(new GitHubAuthSession
            {
                SessionId = session.SessionId,
                GitHubLogin = session.GitHubLogin,
                DisplayName = session.DisplayName ?? string.Empty,
                AvatarUrl = session.AvatarUrl ?? string.Empty,
                ProtectedAccessToken = _protector.Protect(session.AccessToken),
                CreatedUtc = session.CreatedUtc.UtcDateTime,
                ExpiresUtc = expiresUtc,
                LastAccessedUtc = now
            });
        }
        else
        {
            existing.GitHubLogin = session.GitHubLogin;
            existing.DisplayName = session.DisplayName ?? string.Empty;
            existing.AvatarUrl = session.AvatarUrl ?? string.Empty;
            existing.ProtectedAccessToken = _protector.Protect(session.AccessToken);
            existing.ExpiresUtc = expiresUtc;
            existing.LastAccessedUtc = now;
        }

        await db.SaveChangesAsync();
    }

    public async Task<GitHubTokenSession?> GetAsync(string sessionId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var existing = await db.GitHubAuthSessions.FirstOrDefaultAsync(current => current.SessionId == sessionId);
        if (existing is null)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        if (existing.ExpiresUtc <= now)
        {
            db.GitHubAuthSessions.Remove(existing);
            await db.SaveChangesAsync();
            return null;
        }

        existing.LastAccessedUtc = now;
        await db.SaveChangesAsync();

        return new GitHubTokenSession(
            existing.SessionId,
            _protector.Unprotect(existing.ProtectedAccessToken),
            existing.GitHubLogin,
            string.IsNullOrWhiteSpace(existing.DisplayName) ? null : existing.DisplayName,
            string.IsNullOrWhiteSpace(existing.AvatarUrl) ? null : existing.AvatarUrl,
            new DateTimeOffset(existing.CreatedUtc, TimeSpan.Zero));
    }

    public async Task RemoveAsync(string sessionId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var existing = await db.GitHubAuthSessions.FirstOrDefaultAsync(current => current.SessionId == sessionId);
        if (existing is null)
        {
            return;
        }

        db.GitHubAuthSessions.Remove(existing);
        await db.SaveChangesAsync();
    }
}