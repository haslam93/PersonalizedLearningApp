namespace UpskillTracker.Services;

public sealed class GitHubOAuthOptions
{
    public const string SectionName = "GitHubOAuth";
    public const string AuthenticationScheme = "GitHub";

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string CallbackPath { get; set; } = "/signin-github";

    public string[] Scopes { get; set; } = ["read:user"];

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret);
}