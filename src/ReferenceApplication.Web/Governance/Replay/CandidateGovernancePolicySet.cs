using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AsiBackbone.Core.Constraints;
using AsiBackbone.Core.Decisions;
using AsiBackbone.Core.Evaluation;
using Microsoft.Extensions.Options;
using ReferenceApplication.Web.Authentication.Options;

namespace ReferenceApplication.Web.Governance.Replay;

/// <summary>
/// A stand-in "next version" of the sample policy set, defined only so decision replay can show what a policy change
/// would have done to past decisions.
/// </summary>
/// <remarks>
/// The governed endpoints never evaluate this policy. Decision replay composes it on top of the registered constraints
/// and decision policy for a what-if evaluation, and labels every result as hypothetical.
/// </remarks>
public static class CandidateGovernancePolicySet
{
    private const string _canonicalDefinition =
        "referenceapplication-governance-policy-set\n" +
        "version=1.1.0\n" +
        "administrator-action=authenticated actor has a configured administrator role\n" +
        "consequential-action=explicit acknowledgment required\n" +
        "capability-protected-action=endpoint declares scope referenceapplication.consequential.execute; " +
        "authenticated actor has a configured administrator role\n" +
        "audited-record=explicit acknowledgment required";

    /// <summary>
    /// Gets the version of the stand-in policy.
    /// </summary>
    public const string Version = "1.1.0";

    /// <summary>
    /// Gets the stable hash of the stand-in policy's canonical definition.
    /// </summary>
    public static string PolicyHash { get; } = CreatePolicyHash();

    /// <summary>
    /// Gets what the stand-in policy changes from the current one.
    /// </summary>
    public static IReadOnlyList<string> Changes { get; } =
    [
        "Capability-protected actions also require a configured administrator role.",
        "Creating an audited record also requires explicit acknowledgment."
    ];

    private static string CreatePolicyHash()
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(_canonicalDefinition));
        return $"sha256:{Convert.ToHexStringLower(digest)}";
    }
}

/// <summary>
/// The stand-in policy's added rule: a capability-protected action also requires an administrator role.
/// </summary>
internal sealed class CandidateCapabilityAdministratorConstraint(
    IHttpContextAccessor httpContextAccessor,
    IOptions<ApplicationAuthorizationOptions> authorizationOptionsAccessor) :
    IGovernanceConstraint<GovernanceEvaluationContext>
{
    /// <summary>
    /// Gets the stable name of this constraint.
    /// </summary>
    public const string ConstraintName = "referenceapplication.candidate.capability_administrator";

    /// <summary>
    /// Gets the reason code returned when the actor does not satisfy the rule.
    /// </summary>
    public const string DeniedReasonCode = "referenceapplication.candidate.capability_administrator_required";

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

        if (!GovernanceMetadata.ContainsPolicy<CapabilityProtectedActionPolicy>(context.Metadata))
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
                "Under the stand-in policy, a capability-protected action also requires a configured administrator role."));
    }
}

/// <summary>
/// The stand-in policy's decision policy: the registered one, and then an acknowledgment requirement for creating an
/// audited record.
/// </summary>
/// <param name="inner">The registered decision policy.</param>
/// <param name="auditedRecordOperationName">The operation name of the create-audited-record endpoint.</param>
internal sealed class CandidateDecisionPolicy(
    IGovernanceDecisionPolicy<GovernanceEvaluationContext> inner,
    string auditedRecordOperationName) : IGovernanceDecisionPolicy<GovernanceEvaluationContext>
{
    /// <summary>
    /// Gets the reason code returned when the stand-in policy requires acknowledgment for an audited record.
    /// </summary>
    public const string AuditedRecordAcknowledgmentReasonCode =
        "referenceapplication.candidate.audited_record_acknowledgment_required";

    private const string _operationNameMetadataKey = "endpoint.operation_name";

    private readonly IGovernanceDecisionPolicy<GovernanceEvaluationContext> _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public async ValueTask<GovernanceDecision> ApplyAsync(
        GovernanceEvaluationContext context,
        GovernanceDecision composedDecision,
        IReadOnlyList<ConstraintEvaluationResult> constraintResults,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        GovernanceDecision decision = await _inner
            .ApplyAsync(context, composedDecision, constraintResults, cancellationToken)
            .ConfigureAwait(false);

        bool isAuditedRecord = context.Metadata.TryGetValue(_operationNameMetadataKey, out string? operationName)
            && string.Equals(operationName, auditedRecordOperationName, StringComparison.Ordinal);

        return !decision.CanProceed || !isAuditedRecord
            ? decision
            : GovernanceDecision.RequireAcknowledgment(
                AuditedRecordAcknowledgmentReasonCode,
                "Under the stand-in policy, creating an audited record requires explicit acknowledgment.",
                correlationId: context.CorrelationId,
                policyVersion: context.PolicyVersion,
                policyHash: context.PolicyHash);
    }
}
