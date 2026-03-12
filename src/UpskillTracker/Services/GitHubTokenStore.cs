using Microsoft.Extensions.Caching.Memory;

namespace UpskillTracker.Services;

public sealed class GitHubTokenStore(IMemoryCache cache)
{
    public void Store(GitHubTokenSession session, TimeSpan? lifetime = null)
    {
        cache.Set(session.SessionId, session, new MemoryCacheEntryOptions
        {
            SlidingExpiration = lifetime ?? TimeSpan.FromHours(8)
        });
    }

    public bool TryGet(string sessionId, out GitHubTokenSession? session)
    {
        if (cache.TryGetValue(sessionId, out GitHubTokenSession? value))
        {
            session = value;
            return true;
        }

        session = null;
        return false;
    }

    public void Remove(string sessionId)
    {
        cache.Remove(sessionId);
    }
}