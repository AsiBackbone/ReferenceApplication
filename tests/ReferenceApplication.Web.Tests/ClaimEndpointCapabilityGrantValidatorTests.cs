using System.Security.Claims;
using AsiBackbone.AspNetCore.Endpoints;
using AsiBackbone.Core.Decisions;
using Microsoft.AspNetCore.Http;
using ReferenceApplication.Web.Authentication.Options;
using ReferenceApplication.Web.Governance;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies the host-owned claim-backed endpoint capability validator in isolation.
/// </summary>
public sealed class ClaimEndpointCapabilityGrantValidatorTests
{
    private const string _permissionClaimType = "test:capability";

    /// <summary>
    /// Verifies that every declared scope must be present as an exact claim on an authenticated identity.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_AllDeclaredScopesGranted_PreservesAllowedDecision()
    {
        ClaimEndpointCapabilityGrantValidator validator = CreateValidator();
        DefaultHttpContext httpContext = CreateContext(
            new Claim(_permissionClaimType, "sample.one"),
            new Claim(_permissionClaimType, "sample.two"));
        EndpointGovernanceDescriptor descriptor = CreateDescriptor("sample.one", "sample.two");
        var currentDecision = GovernanceDecision.Allow(correlationId: "correlation-1");

        GovernanceDecision result = await validator.ValidateAsync(
            httpContext,
            descriptor,
            currentDecision,
            TestContext.Current.CancellationToken);

        Assert.Same(currentDecision, result);
    }

    /// <summary>
    /// Verifies that a missing scope denies the request without disclosing which grant was absent.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_DeclaredScopeMissing_Denies()
    {
        ClaimEndpointCapabilityGrantValidator validator = CreateValidator();
        DefaultHttpContext httpContext = CreateContext(new Claim(_permissionClaimType, "sample.one"));
        EndpointGovernanceDescriptor descriptor = CreateDescriptor("sample.one", "sample.two");

        GovernanceDecision result = await validator.ValidateAsync(
            httpContext,
            descriptor,
            GovernanceDecision.Allow(correlationId: "correlation-1"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsDenied);
        Assert.Equal(ClaimEndpointCapabilityGrantValidator.DeniedReasonCode, Assert.Single(result.ReasonCodes));
        Assert.Equal("correlation-1", result.CorrelationId);
    }

    /// <summary>
    /// Verifies that capability validation never weakens an earlier blocking decision.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_CurrentDecisionDenied_PreservesDecision()
    {
        ClaimEndpointCapabilityGrantValidator validator = CreateValidator();
        DefaultHttpContext httpContext = CreateContext();
        EndpointGovernanceDescriptor descriptor = CreateDescriptor("sample.one");
        var currentDecision = GovernanceDecision.Deny("existing.denial", "Already denied.");

        GovernanceDecision result = await validator.ValidateAsync(
            httpContext,
            descriptor,
            currentDecision,
            TestContext.Current.CancellationToken);

        Assert.Same(currentDecision, result);
    }

    /// <summary>
    /// Verifies that a missing grant denies a decision that already requires acknowledgment. AsiBackbone runs the
    /// validator after policy evaluation, so acknowledgment must never stand in for a missing grant.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_AcknowledgmentRequiredWithoutGrant_Denies()
    {
        ClaimEndpointCapabilityGrantValidator validator = CreateValidator();
        DefaultHttpContext httpContext = CreateContext();
        EndpointGovernanceDescriptor descriptor = CreateDescriptor("sample.one");
        var currentDecision = GovernanceDecision.RequireAcknowledgment(
            "existing.acknowledgment",
            "Acknowledgment required.",
            correlationId: "correlation-1");

        GovernanceDecision result = await validator.ValidateAsync(
            httpContext,
            descriptor,
            currentDecision,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsDenied);
        Assert.False(result.RequiresAcknowledgment);
        Assert.Equal(ClaimEndpointCapabilityGrantValidator.DeniedReasonCode, Assert.Single(result.ReasonCodes));
        Assert.Equal("correlation-1", result.CorrelationId);
    }

    /// <summary>
    /// Verifies that an actor holding every grant keeps an acknowledgment-required decision unchanged.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_AcknowledgmentRequiredWithGrant_PreservesDecision()
    {
        ClaimEndpointCapabilityGrantValidator validator = CreateValidator();
        DefaultHttpContext httpContext = CreateContext(new Claim(_permissionClaimType, "sample.one"));
        EndpointGovernanceDescriptor descriptor = CreateDescriptor("sample.one");
        var currentDecision = GovernanceDecision.RequireAcknowledgment(
            "existing.acknowledgment",
            "Acknowledgment required.",
            correlationId: "correlation-1");

        GovernanceDecision result = await validator.ValidateAsync(
            httpContext,
            descriptor,
            currentDecision,
            TestContext.Current.CancellationToken);

        Assert.Same(currentDecision, result);
    }

    /// <summary>
    /// Verifies that a grant claim on an unauthenticated identity cannot satisfy a declared scope.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_GrantOnUnauthenticatedIdentity_Denies()
    {
        ClaimEndpointCapabilityGrantValidator validator = CreateValidator();
        DefaultHttpContext httpContext = new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(_permissionClaimType, "sample.one")],
                authenticationType: null))
        };
        EndpointGovernanceDescriptor descriptor = CreateDescriptor("sample.one");

        GovernanceDecision result = await validator.ValidateAsync(
            httpContext,
            descriptor,
            GovernanceDecision.Allow(correlationId: "correlation-1"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsDenied);
        Assert.Equal(ClaimEndpointCapabilityGrantValidator.DeniedReasonCode, Assert.Single(result.ReasonCodes));
    }

    private static ClaimEndpointCapabilityGrantValidator CreateValidator()
    {
        return new ClaimEndpointCapabilityGrantValidator(Microsoft.Extensions.Options.Options.Create(
            new ApplicationAuthorizationOptions
            {
                PermissionClaimType = _permissionClaimType
            }));
    }

    private static DefaultHttpContext CreateContext(params Claim[] claims)
    {
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"))
        };
    }

    private static EndpointGovernanceDescriptor CreateDescriptor(params string[] scopes)
    {
        object[] metadata = [.. scopes.Select(static scope => (object)new RequireCapabilityGrantAttribute(scope))];
        Endpoint endpoint = new(
            static _ => Task.CompletedTask,
            new EndpointMetadataCollection(metadata),
            "Capability validator test endpoint");

        return EndpointGovernanceDescriptor.FromEndpoint(endpoint);
    }
}
