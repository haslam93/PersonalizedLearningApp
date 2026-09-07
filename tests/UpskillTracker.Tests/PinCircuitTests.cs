using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using UpskillTracker.Services;

namespace UpskillTracker.Tests;

public sealed class PinCircuitTests
{
    [Fact]
    public async Task Circuit_events_and_reconnect_require_a_current_server_session()
    {
        var configuration = TestConfiguration();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = new AdjustableTimeProvider();
        var sessions = new PinSessionService(configuration, cache, clock);
        var ticket = Assert.IsType<Microsoft.AspNetCore.Authentication.AuthenticationTicket>(
            sessions.CreateTicket(PortalApplicationFactory.TestPin));
        var state = new FixedAuthenticationStateProvider(ticket.Principal);
        var circuit = new PinCircuitHandler(state, sessions);
        var calls = 0;
        var inbound = circuit.CreateInboundActivityHandler(_ =>
        {
            calls++;
            return Task.CompletedTask;
        });

        await circuit.OnCircuitOpenedAsync(null!, CancellationToken.None);
        await circuit.OnConnectionUpAsync(null!, CancellationToken.None);
        await inbound(null!);
        Assert.Equal(1, calls);

        clock.Advance(PinSessionService.Lifetime);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inbound(null!));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => circuit.OnConnectionUpAsync(null!, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Logout_and_pin_rotation_revoke_already_connected_circuits()
    {
        var configuration = TestConfiguration();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sessions = new PinSessionService(configuration, cache, TimeProvider.System);
        var ticket = sessions.CreateTicket(PortalApplicationFactory.TestPin)!;
        var circuit = new PinCircuitHandler(new FixedAuthenticationStateProvider(ticket.Principal), sessions);
        sessions.Revoke(ticket.Principal);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => circuit.OnConnectionUpAsync(null!, CancellationToken.None));

        var newTicket = sessions.CreateTicket(PortalApplicationFactory.TestPin)!;
        var newCircuit = new PinCircuitHandler(new FixedAuthenticationStateProvider(newTicket.Principal), sessions);
        configuration["AccessPin"] = "654321";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => newCircuit.OnConnectionUpAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Anonymous_and_github_only_circuits_cannot_execute_events()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sessions = new PinSessionService(TestConfiguration(), cache, TimeProvider.System);
        var ticket = sessions.CreateTicket(PortalApplicationFactory.TestPin)!;
        var githubWithCopiedClaim = new ClaimsPrincipal(new ClaimsIdentity(ticket.Principal.Claims, "Cookies"));

        foreach (var principal in new[] { new ClaimsPrincipal(new ClaimsIdentity()), githubWithCopiedClaim })
        {
            var handler = new PinCircuitHandler(new FixedAuthenticationStateProvider(principal), sessions);
            var invoked = false;
            var inbound = handler.CreateInboundActivityHandler(_ =>
            {
                invoked = true;
                return Task.CompletedTask;
            });
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inbound(null!));
            Assert.False(invoked);
        }
    }

    [Fact]
    public async Task Idle_pin_only_circuits_revalidate_without_marking_copilot_signed_in()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sessions = new PinSessionService(TestConfiguration(), cache, TimeProvider.System);
        var ticket = sessions.CreateTicket(PortalApplicationFactory.TestPin)!;
        var principal = new ClaimsPrincipal(new ClaimsIdentity());
        principal.AddIdentities(ticket.Principal.Identities);
        Assert.False(principal.Identity!.IsAuthenticated);

        using var provider = new PinCircuitAuthenticationStateProvider(sessions, TimeProvider.System);
        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));
        var revoked = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.AuthenticationStateChanged += async state => revoked.TrySetResult(await state);
        sessions.Revoke(principal);

        var state = await revoked.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(sessions.HasAccess(state.User));
        Assert.DoesNotContain(state.User.Identities, identity => identity.AuthenticationType == PinSessionService.Scheme);
    }

    [Fact]
    public void Restarted_server_does_not_trust_a_session_from_another_registry()
    {
        using var firstCache = new MemoryCache(new MemoryCacheOptions());
        using var secondCache = new MemoryCache(new MemoryCacheOptions());
        var first = new PinSessionService(TestConfiguration(), firstCache, TimeProvider.System);
        var restarted = new PinSessionService(TestConfiguration(), secondCache, TimeProvider.System);
        Assert.False(restarted.HasAccess(first.CreateTicket(PortalApplicationFactory.TestPin)!.Principal));
    }

    private static IConfigurationRoot TestConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["AccessPin"] = PortalApplicationFactory.TestPin })
        .Build();

    private sealed class FixedAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }
}
