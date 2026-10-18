using AsiBackbone.Core.Constraints;
using AsiBackbone.Core.Decisions;
using AsiBackbone.Core.Evaluation;

namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Converts otherwise-allowed consequential operations into acknowledgment-required decisions.
/// </summary>
public sealed class ConsequentialActionDecisionPolicy : IGovernanceDecisionPolicy<GovernanceEvaluationContext>
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

        return ValueTask.FromResult(!composedDecision.CanProceed
            || !GovernanceMetadata.ContainsPolicy<ConsequentialActionPolicy>(context.Metadata)
                ? composedDecision
                : GovernanceDecision.RequireAcknowledgment(
                    AcknowledgmentReasonCode,
                    "The consequential operation requires explicit acknowledgment before execution.",
                    correlationId: context.CorrelationId,
                    policyVersion: context.PolicyVersion,
                    policyHash: context.PolicyHash));
    }
}
