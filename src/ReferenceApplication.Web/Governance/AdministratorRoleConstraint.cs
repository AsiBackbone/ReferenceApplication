using System.Security.Claims;
using AsiBackbone.Core.Constraints;
using Microsoft.Extensions.Options;
using ReferenceApplication.Web.Authentication.Options;

namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Requires administrator-policy operations to be evaluated for an authenticated actor in a configured administrator
/// role.
/// </summary>
public sealed class AdministratorRoleConstraint(
    IHttpContextAccessor httpContextAccessor,
    IOptions<ApplicationAuthorizationOptions> authorizationOptionsAccessor) :
    IGovernanceConstraint<GovernanceEvaluationContext>
{
    /// <summary>
    /// Gets the stable name of this constraint.
    /// </summary>
    public const string ConstraintName = "referenceapplication.actor.administrator";

    /// <summary>
    /// Gets the machine-readable reason code returned when the actor does not satisfy the rule.
    /// </summary>
    public const string DeniedReasonCode = "referenceapplication.actor.administrator_required";

    private readonly IHttpContextAccessor _httpContextAccessor =
        httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
    private readonly IOptions<ApplicationAuthorizationOptions> _authorizationOptionsAccessor =
        authorizationOptionsAccessor ?? throw new ArgumentNullException(nameof(authorizationOptionsAccessor));

    /// <inheritdoc />
    public string Name => ConstraintName;

    /// <inheritdoc />
    public ValueTask<ConstraintEvaluationResult> EvaluateAsync(
        GovernanceEvaluationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (!GovernanceMetadata.ContainsPolicy<AdministratorActionPolicy>(context.Metadata))
        {
            return ValueTask.FromResult(ConstraintEvaluationResult.NotApplicable());
        }

        ClaimsPrincipal? actor = _httpContextAccessor.HttpContext?.User;
        ApplicationAuthorizationOptions options = _authorizationOptionsAccessor.Value;

        bool hasAdministratorRole = actor?.Identities
            .Where(static identity => identity.IsAuthenticated)
            .SelectMany(static identity => identity.Claims)
            .Any(claim =>
                string.Equals(claim.Type, options.RoleClaimType, StringComparison.Ordinal)
                && options.AdministratorRoles.Contains(claim.Value, StringComparer.Ordinal)) == true;

        return ValueTask.FromResult(hasAdministratorRole
            ? ConstraintEvaluationResult.Allow()
            : ConstraintEvaluationResult.Deny(
                DeniedReasonCode,
                "The operation requires an authenticated actor in a configured administrator role."));
    }
}
