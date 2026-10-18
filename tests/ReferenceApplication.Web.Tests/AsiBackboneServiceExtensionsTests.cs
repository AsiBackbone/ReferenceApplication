using System.Security.Claims;
using AsiBackbone.AspNetCore.Acknowledgments;
using AsiBackbone.AspNetCore.Actors;
using AsiBackbone.AspNetCore.Correlation;
using AsiBackbone.AspNetCore.Endpoints;
using AsiBackbone.Core.Actors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReferenceApplication.Web.Authentication.Claims;
using ReferenceApplication.Web.Extensions;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Provides unit tests for <see cref="AsiBackboneServiceExtensions.AddApplicationAsiBackbone"/>, the application-owned
/// AsiBackbone service composition added in walkthrough Step 3.
/// </summary>
public sealed class AsiBackboneServiceExtensionsTests
{
    /// <summary>
    /// Verifies that registration rejects a null service collection.
    /// </summary>
    [Fact]
    public void AddApplicationAsiBackbone_NullServices_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => AsiBackboneServiceExtensions.AddApplicationAsiBackbone(null!));
    }

    /// <summary>
    /// Verifies that registration returns the same service collection so host registrations can be chained.
    /// </summary>
    [Fact]
    public void AddApplicationAsiBackbone_ReturnsSameServiceCollection()
    {
        ServiceCollection services = new();

        IServiceCollection result = services.AddApplicationAsiBackbone();

        Assert.Same(services, result);
    }

    /// <summary>
    /// Verifies that each framework integration service selected in Step 3 resolves from a request scope with scope
    /// validation enabled.
    /// </summary>
    /// <param name="serviceType">The AsiBackbone integration service type to resolve.</param>
    [Theory]
    [InlineData(typeof(IHttpGovernanceActorContextResolver))]
    [InlineData(typeof(IHttpGovernanceRequestCorrelationResolver))]
    [InlineData(typeof(IAcknowledgmentChallengeService))]
    [InlineData(typeof(IEndpointGovernanceService))]
    public void AddApplicationAsiBackbone_ResolvesScopedIntegrationService(Type serviceType)
    {
        using ServiceProvider provider = CreateServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        object service = scope.ServiceProvider.GetRequiredService(serviceType);

        Assert.IsAssignableFrom(serviceType, service);
    }

    /// <summary>
    /// Verifies that the application's normalized subject claim is checked first for the actor identifier and that
    /// AsiBackbone's standard identifier claims remain as fallbacks in their original order.
    /// </summary>
    [Fact]
    public void AddApplicationAsiBackbone_PrefersApplicationSubjectClaim_AndKeepsDefaultFallbacks()
    {
        using ServiceProvider provider = CreateServiceProvider();

        HttpGovernanceActorContextOptions options =
            provider.GetRequiredService<IOptions<HttpGovernanceActorContextOptions>>().Value;

        Assert.Equal(
            [ApplicationClaimTypes.Subject, .. HttpGovernanceActorContextOptions.DefaultActorIdClaimTypes],
            options.ActorIdClaimTypes);
    }

    /// <summary>
    /// Verifies that the application's normalized name claim is checked first for the display name and that
    /// AsiBackbone's standard display-name claims remain as fallbacks in their original order.
    /// </summary>
    [Fact]
    public void AddApplicationAsiBackbone_PrefersApplicationNameClaim_AndKeepsDefaultFallbacks()
    {
        using ServiceProvider provider = CreateServiceProvider();

        HttpGovernanceActorContextOptions options =
            provider.GetRequiredService<IOptions<HttpGovernanceActorContextOptions>>().Value;

        Assert.Equal(
            [ApplicationClaimTypes.Name, .. HttpGovernanceActorContextOptions.DefaultDisplayNameClaimTypes],
            options.DisplayNameClaimTypes);
    }

    /// <summary>
    /// Verifies that normalized NCAT subject and name claims resolve to a known, authenticated human governance actor.
    /// </summary>
    [Fact]
    public void ResolveActorContext_NormalizedClaims_ReturnsKnownAuthenticatedHumanActor()
    {
        IGovernanceActorContext actor = ResolveActor(CreateAuthenticatedPrincipal(
            new Claim(ApplicationClaimTypes.Subject, "user-123"),
            new Claim(ApplicationClaimTypes.Name, "Ada Lovelace")));

        Assert.Equal("user-123", actor.ActorId);
        Assert.Equal("Ada Lovelace", actor.DisplayName);
        Assert.Equal(GovernanceActorType.Human, actor.ActorType);
        Assert.True(actor.IsAuthenticated);
        Assert.True(actor.IsKnown);
    }

    /// <summary>
    /// Verifies that normalized application claims take precedence over provider-specific identifier and name
    /// claims on the same principal.
    /// </summary>
    [Fact]
    public void ResolveActorContext_NormalizedAndProviderClaims_PrefersNormalizedClaims()
    {
        IGovernanceActorContext actor = ResolveActor(CreateAuthenticatedPrincipal(
            new Claim(ClaimTypes.NameIdentifier, "provider-id"),
            new Claim("sub", "provider-subject"),
            new Claim(ClaimTypes.Name, "Provider Display Name"),
            new Claim(ApplicationClaimTypes.Subject, "user-123"),
            new Claim(ApplicationClaimTypes.Name, "Ada Lovelace")));

        Assert.Equal("user-123", actor.ActorId);
        Assert.Equal("Ada Lovelace", actor.DisplayName);
    }

    /// <summary>
    /// Verifies that a host which disables or customizes the NCAT claims transformation still resolves an actor from
    /// the standard identifier and name claims.
    /// </summary>
    [Fact]
    public void ResolveActorContext_WithoutNormalizedClaims_FallsBackToStandardClaims()
    {
        IGovernanceActorContext actor = ResolveActor(CreateAuthenticatedPrincipal(
            new Claim(ClaimTypes.NameIdentifier, "user-456"),
            new Claim(ClaimTypes.Name, "Grace Hopper")));

        Assert.Equal("user-456", actor.ActorId);
        Assert.Equal("Grace Hopper", actor.DisplayName);
        Assert.True(actor.IsAuthenticated);
        Assert.True(actor.IsKnown);
    }

    /// <summary>
    /// Verifies that an unauthenticated principal is not presented as an authenticated or known actor, even when it
    /// carries an application subject claim.
    /// </summary>
    [Fact]
    public void ResolveActorContext_UnauthenticatedPrincipal_ReturnsUnauthenticatedActor()
    {
        ClaimsPrincipal principal = new(new ClaimsIdentity(
            [new Claim(ApplicationClaimTypes.Subject, "user-123")],
            authenticationType: null));

        IGovernanceActorContext actor = ResolveActor(principal);

        Assert.False(actor.IsAuthenticated);
        Assert.False(actor.IsKnown);
        Assert.NotEqual("user-123", actor.ActorId);
    }

    private static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddApplicationAsiBackbone();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    private static IGovernanceActorContext ResolveActor(ClaimsPrincipal principal)
    {
        using ServiceProvider provider = CreateServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        IHttpContextAccessor accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext { User = principal };

        try
        {
            return scope.ServiceProvider.GetRequiredService<IHttpGovernanceActorContextResolver>().ResolveActorContext();
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    private static ClaimsPrincipal CreateAuthenticatedPrincipal(params Claim[] claims)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }
}
