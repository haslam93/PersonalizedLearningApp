using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace UpskillTracker.Services;

public sealed class CopilotAuthService(IOptions<GitHubOAuthOptions> gitHubOptions, GitHubTokenStore tokenStore)
{
    public async Task<CopilotAuthState> GetStateAsync(ClaimsPrincipal user)
    {
        var isSignedIn = user.Identity?.IsAuthenticated ?? false;
        var sessionId = user.FindFirstValue(CopilotAuthClaims.SessionId);
        var hasToken = !string.IsNullOrWhiteSpace(sessionId) && await tokenStore.GetAsync(sessionId) is not null;

        return new CopilotAuthState(
            gitHubOptions.Value.IsConfigured,
            isSignedIn,
            hasToken,
            sessionId,
            user.FindFirstValue(ClaimTypes.Name),
            user.FindFirstValue(CopilotAuthClaims.GitHubLogin),
            user.FindFirstValue(CopilotAuthClaims.AvatarUrl));
    }
}