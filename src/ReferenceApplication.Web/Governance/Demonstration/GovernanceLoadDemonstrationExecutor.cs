using AsiBackbone.Core.Constraints;
using AsiBackbone.Core.Evaluation;

namespace ReferenceApplication.Web.Governance.Demonstration;

/// <summary>
/// Exercises the host's real composed evaluator without persisting synthetic decision evidence.
/// </summary>
internal sealed class GovernanceLoadDemonstrationExecutor(
    IGovernancePolicyEvaluator<GovernanceEvaluationContext> evaluator) : IGovernanceLoadDemonstrationExecutor
{
    private static readonly IReadOnlyDictionary<string, string> _metadata =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["endpoint.policy_types"] = typeof(CapabilityProtectedActionPolicy).FullName!,
            ["endpoint.capability_scopes"] = ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope,
            ["demonstration.kind"] = "bounded-live-governance-load"
        };

    private readonly IGovernancePolicyEvaluator<GovernanceEvaluationContext> _evaluator =
        evaluator ?? throw new ArgumentNullException(nameof(evaluator));

    public async ValueTask EvaluateAsync(string correlationId, CancellationToken cancellationToken)
    {
        var context = new GovernanceEvaluationContext(
            correlationId,
            ReferenceApplicationGovernancePolicySet.Version,
            ReferenceApplicationGovernancePolicySet.PolicyHash,
            _metadata);
        _ = await _evaluator.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
    }
}
