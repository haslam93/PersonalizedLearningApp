namespace UpskillTracker.Services;

public class YouTubeOptions
{
    public const string SectionName = "YouTube";

    public string ApiKey { get; set; } = string.Empty;

    public int MaxVideosPerChannel { get; set; } = 12;
}