namespace UpskillTracker.Services;

public sealed record CopilotAuthState(
    bool IsGitHubConfigured,
    bool IsSignedIn,
    bool HasCopilotToken,
    string? SessionId,
    string? DisplayName,
    string? GitHubLogin,
    string? AvatarUrl)
{
    public bool CanUseCopilot => IsGitHubConfigured && IsSignedIn && HasCopilotToken && !string.IsNullOrWhiteSpace(SessionId);
}