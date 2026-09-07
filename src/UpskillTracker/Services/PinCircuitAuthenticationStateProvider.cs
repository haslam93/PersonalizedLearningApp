using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace UpskillTracker.Services;

public sealed class PinAccessRequirement : IAuthorizationRequirement;

public sealed class PinAccessAuthorizationHandler(PinSessionService sessions)
    : AuthorizationHandler<PinAccessRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PinAccessRequirement requirement)
    {
        if (sessions.HasAccess(context.User))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public sealed class PinCircuitAuthenticationStateProvider : ServerAuthenticationStateProvider, IDisposable
{
    private readonly PinSessionService sessions;
    private readonly TimeProvider clock;
    private CancellationTokenSource? revalidation;

    public PinCircuitAuthenticationStateProvider(PinSessionService sessions, TimeProvider clock)
    {
        this.sessions = sessions;
        this.clock = clock;
        AuthenticationStateChanged += OnAuthenticationStateChanged;
    }

    private void OnAuthenticationStateChanged(Task<AuthenticationState> state)
    {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref revalidation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        _ = RevalidateAsync(state, cancellation.Token);
    }

    private async Task RevalidateAsync(Task<AuthenticationState> stateTask, CancellationToken cancellationToken)
    {
        try
        {
            var state = await stateTask.WaitAsync(cancellationToken);
            if (!state.User.Identities.Any(identity => identity.AuthenticationType == PinSessionService.Scheme))
            {
                return;
            }

            // The primary GitHub identity may be anonymous, but the PIN still needs revalidation.
            while (sessions.HasAccess(state.User))
            {
                await Task.Delay(TimeSpan.FromSeconds(10), clock, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            SetAuthenticationState(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                state.User.Identities.Where(identity => identity.AuthenticationType != PinSessionService.Scheme)))));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        AuthenticationStateChanged -= OnAuthenticationStateChanged;
        var cancellation = Interlocked.Exchange(ref revalidation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }
}

public sealed class PinCircuitHandler(
    AuthenticationStateProvider authenticationStateProvider, PinSessionService sessions) : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken) =>
        RequireAccessAsync(cancellationToken);

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken) =>
        RequireAccessAsync(cancellationToken);

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next) => async context =>
    {
        // Cookie middleware doesn't run for events on an already-connected Blazor circuit.
        await RequireAccessAsync(CancellationToken.None);
        await next(context);
    };

    private async Task RequireAccessAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        if (!sessions.HasAccess(state.User))
        {
            throw new UnauthorizedAccessException("The portal access session has expired. Reload to enter your PIN.");
        }
    }
}
