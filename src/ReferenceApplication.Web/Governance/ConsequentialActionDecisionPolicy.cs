using AsiBackbone.Core.Constraints;
using AsiBackbone.Core.Decisions;
using AsiBackbone.Core.Evaluation;

namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Converts otherwise-allowed consequential operations into acknowledgment-required decisions.
/// </summary>
public sealed class ConsequentialActionDecisionPolicy(IHttpContextAccessor? httpContextAccessor = null)
    : IGovernanceDecisionPolicy<GovernanceEvaluationContext>
{
    /// <summary>
    /// Gets the machine-readable reason code returned for consequential operations.
    /// </summary>
    public const string AcknowledgmentReasonCode = "referenceapplication.consequential.acknowledgment_required";

    /// <inheritdoc />
    public ValueTask<GovernanceDecision> ApplyAsync(
        GovernanceEvaluationContext context,
        GovernanceDecision composedDecision,
        IReadOnlyList<ConstraintEvaluationResult> constraintResults,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(composedDecision);
        ArgumentNullException.ThrowIfNull(constraintResults);
        cancellationToken.ThrowIfCancellationRequested();

        if (!composedDecision.CanProceed
            || !GovernanceMetadata.ContainsPolicy<ConsequentialActionPolicy>(context.Metadata))
        {
            return ValueTask.FromResult(composedDecision);
        }

        // The middleware creates this feature only after matching a persisted, accepted, unexpired acknowledgment
        // to the current actor and selected endpoint. Simulators and ordinary requests have no feature and still
        // produce the normal acknowledgment-required decision.
        return httpContextAccessor?.HttpContext?.Features.Get<IApplicationAcknowledgmentFeature>() is not null
            ? ValueTask.FromResult(composedDecision)
            : ValueTask.FromResult(GovernanceDecision.RequireAcknowledgment(
            AcknowledgmentReasonCode,
            "The consequential operation requires explicit acknowledgment before execution.",
            correlationId: context.CorrelationId,
            policyVersion: context.PolicyVersion,
            policyHash: context.PolicyHash));
    }
}
