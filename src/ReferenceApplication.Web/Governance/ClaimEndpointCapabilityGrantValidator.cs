using AsiBackbone.AspNetCore.Endpoints;
using AsiBackbone.Core.Decisions;
using Microsoft.Extensions.Options;
using ReferenceApplication.Web.Authentication.Options;

namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Validates endpoint capability scopes against the current actor's normalized application permission claims.
/// </summary>
public sealed class ClaimEndpointCapabilityGrantValidator(
    IOptions<ApplicationAuthorizationOptions> authorizationOptionsAccessor) : IEndpointCapabilityGrantValidator
{
    /// <summary>
    /// Gets the machine-readable reason code returned when the actor lacks a required endpoint capability.
    /// </summary>
    public const string DeniedReasonCode = "referenceapplication.capability.grant_required";

    private readonly IOptions<ApplicationAuthorizationOptions> _authorizationOptionsAccessor =
        authorizationOptionsAccessor ?? throw new ArgumentNullException(nameof(authorizationOptionsAccessor));

    /// <inheritdoc />
    public ValueTask<GovernanceDecision> ValidateAsync(
        HttpContext httpContext,
        EndpointGovernanceDescriptor descriptor,
        GovernanceDecision currentDecision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(currentDecision);
        cancellationToken.ThrowIfCancellationRequested();

        // AsiBackbone calls this validator after policy evaluation, so the current decision may already require
        // acknowledgment (or be deferred). Only an existing denial is preserved as-is: every other outcome must still
        // pass the grant check, so a missing grant can never be converted into an acknowledgment challenge.
        if (currentDecision.IsDenied)
        {
            return ValueTask.FromResult(currentDecision);
        }

        string permissionClaimType = _authorizationOptionsAccessor.Value.PermissionClaimType;
        var grantedScopes = httpContext.User.Identities
            .Where(static identity => identity.IsAuthenticated)
            .SelectMany(static identity => identity.Claims)
            .Where(claim => string.Equals(claim.Type, permissionClaimType, StringComparison.Ordinal))
            .Select(static claim => claim.Value)
            .ToHashSet(StringComparer.Ordinal);

        bool hasEveryRequiredScope = descriptor.CapabilityScopes.All(grantedScopes.Contains);

        return ValueTask.FromResult(hasEveryRequiredScope
            ? currentDecision
            : GovernanceDecision.Deny(
                DeniedReasonCode,
                "The actor does not hold every capability required by the endpoint.",
                correlationId: currentDecision.CorrelationId,
                traceId: currentDecision.TraceId,
                policyVersion: currentDecision.PolicyVersion,
                policyHash: currentDecision.PolicyHash));
    }
}
