namespace UpskillTracker.Services;

public sealed class TrackerStorageUnavailableException(string message, Exception innerException) : Exception(message, innerException)
{
}