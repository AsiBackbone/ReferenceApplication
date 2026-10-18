using AsiBackbone.Core.Constraints;

namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Fails closed when a capability-protected policy marker is used without the corresponding endpoint capability scope.
/// </summary>
public sealed class CapabilityScopeDeclarationConstraint : IGovernanceConstraint<GovernanceEvaluationContext>
{
    /// <summary>
    /// Gets the stable name of this constraint.
    /// </summary>
    public const string ConstraintName = "referenceapplication.capability.declaration";

    /// <summary>
    /// Gets the machine-readable reason code returned when the required scope is absent.
    /// </summary>
    public const string DeniedReasonCode = "referenceapplication.capability.scope_required";

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

        bool hasRequiredScope = GovernanceMetadata.ContainsCapabilityScope(
            context.Metadata,
            ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope);

        return ValueTask.FromResult(hasRequiredScope
            ? ConstraintEvaluationResult.Allow()
            : ConstraintEvaluationResult.Deny(
                DeniedReasonCode,
                $"The endpoint must declare capability scope '{ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope}'."));
    }
}
