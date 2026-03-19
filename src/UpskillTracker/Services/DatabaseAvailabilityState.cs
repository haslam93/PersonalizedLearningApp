namespace UpskillTracker.Services;

public sealed class DatabaseAvailabilityState
{
    private readonly object syncLock = new();
    private DateTimeOffset? lastFailureUtc;

    public bool IsUnavailable
    {
        get
        {
            lock (syncLock)
            {
                return lastFailureUtc is not null;
            }
        }
    }

    public DateTimeOffset? LastFailureUtc
    {
        get
        {
            lock (syncLock)
            {
                return lastFailureUtc;
            }
        }
    }

    public string UserMessage => "The PostgreSQL database appears to be offline. Start it in Azure, then refresh this page. Tracker data and saves will resume automatically after the next successful connection.";

    public void MarkUnavailable()
    {
        lock (syncLock)
        {
            lastFailureUtc = DateTimeOffset.UtcNow;
        }
    }

    public void MarkAvailable()
    {
        lock (syncLock)
        {
            lastFailureUtc = null;
        }
    }
}