namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Provides the governance decision evidence persisted for the current request, if any.
/// </summary>
public interface IGovernanceDecisionEvidenceAccessor
{
    /// <summary>
    /// Gets the evidence persisted for the current request, or <see langword="null" /> when the request was not
    /// governed with audit emission.
    /// </summary>
    GovernanceDecisionEvidence? Current { get; }
}

/// <summary>
/// Holds the governance decision evidence persisted for the current request scope.
/// </summary>
/// <remarks>
/// <see cref="LedgerDecisionReceiptSink" /> records the evidence after the decision receipt is durably appended to the
/// audit ledger. The evidence is set once per request; a second write in the same scope is rejected so a request can
/// never silently relink its mutations to a different decision.
/// </remarks>
public sealed class GovernanceDecisionEvidenceAccessor : IGovernanceDecisionEvidenceAccessor
{
    /// <inheritdoc />
    public GovernanceDecisionEvidence? Current { get; private set; }

    /// <summary>
    /// Records the evidence persisted for the current request.
    /// </summary>
    /// <param name="evidence">The persisted decision evidence.</param>
    /// <exception cref="InvalidOperationException">Evidence was already recorded for this request.</exception>
    public void Record(GovernanceDecisionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (Current is not null)
        {
            throw new InvalidOperationException(
                "Governance decision evidence was already recorded for this request.");
        }

        Current = evidence;
    }
}
