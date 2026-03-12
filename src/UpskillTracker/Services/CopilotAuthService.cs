using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace UpskillTracker.Services;

public sealed class CopilotAuthService(IOptions<GitHubOAuthOptions> gitHubOptions, GitHubTokenStore tokenStore)
{
    public CopilotAuthState GetState(ClaimsPrincipal user)
    {
        var isSignedIn = user.Identity?.IsAuthenticated ?? false;
        var sessionId = user.FindFirstValue(CopilotAuthClaims.SessionId);
        var hasToken = !string.IsNullOrWhiteSpace(sessionId) && tokenStore.TryGet(sessionId, out _);

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