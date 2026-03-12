namespace UpskillTracker.Services;

public sealed class CopilotSdkOptions
{
    public const string SectionName = "CopilotSdk";

    public string CliPath { get; set; } = string.Empty;

    public string DefaultModel { get; set; } = "gpt-5";
}