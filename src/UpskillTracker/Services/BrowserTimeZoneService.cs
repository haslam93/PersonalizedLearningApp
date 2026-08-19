using Microsoft.JSInterop;

namespace UpskillTracker.Services;

public sealed class BrowserTimeZoneService(IJSRuntime jsRuntime)
{
    private bool initialized;

    public TimeZoneInfo TimeZone { get; private set; } = TimeZoneInfo.Utc;

    public DateTime Today => ToLocal(DateTime.UtcNow).Date;

    public async ValueTask InitializeAsync()
    {
        if (initialized)
        {
            return;
        }

        try
        {
            var timeZoneId = await jsRuntime.InvokeAsync<string?>("upskillTracker.getTimeZoneId");
            TimeZone = ResolveTimeZone(timeZoneId);
        }
        catch (JSDisconnectedException)
        {
            TimeZone = TimeZoneInfo.Utc;
        }
        catch (JSException)
        {
            TimeZone = TimeZoneInfo.Utc;
        }

        initialized = true;
    }

    public DateTime ToLocal(DateTime utc)
    {
        var utcValue = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utcValue, TimeZone);
    }

    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException) when (
            TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out var windowsTimeZoneId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(windowsTimeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
                return TimeZoneInfo.Utc;
            }
            catch (InvalidTimeZoneException)
            {
                return TimeZoneInfo.Utc;
            }
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
