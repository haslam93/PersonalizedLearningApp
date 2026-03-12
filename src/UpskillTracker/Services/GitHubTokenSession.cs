namespace UpskillTracker.Services;

public sealed record GitHubTokenSession(
    string SessionId,
    string AccessToken,
    string GitHubLogin,
    string? DisplayName,
    string? AvatarUrl,
    DateTimeOffset CreatedUtc);