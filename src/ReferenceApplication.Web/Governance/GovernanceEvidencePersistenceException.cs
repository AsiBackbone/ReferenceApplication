namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Thrown when governance decision evidence cannot be durably persisted.
/// </summary>
/// <remarks>
/// The message and reason code are sanitized; provider diagnostics stay in host-controlled logs. Endpoint governance
/// propagates the exception, so a governed operation that requested audit emission never executes without its
/// decision evidence.
/// </remarks>
public sealed class GovernanceEvidencePersistenceException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GovernanceEvidencePersistenceException" /> class.
    /// </summary>
    public GovernanceEvidencePersistenceException()
        : this(LedgerDecisionReceiptSink.AppendFailedReasonCode)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GovernanceEvidencePersistenceException" /> class.
    /// </summary>
    /// <param name="reasonCode">The stable, sanitized reason code.</param>
    public GovernanceEvidencePersistenceException(string reasonCode)
        : base("Governance decision evidence could not be persisted.")
    {
        ReasonCode = string.IsNullOrWhiteSpace(reasonCode)
            ? LedgerDecisionReceiptSink.AppendFailedReasonCode
            : reasonCode;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GovernanceEvidencePersistenceException" /> class.
    /// </summary>
    /// <param name="reasonCode">The stable, sanitized reason code.</param>
    /// <param name="innerException">The underlying persistence exception.</param>
    public GovernanceEvidencePersistenceException(string reasonCode, Exception innerException)
        : base("Governance decision evidence could not be persisted.", innerException)
    {
        ReasonCode = string.IsNullOrWhiteSpace(reasonCode)
            ? LedgerDecisionReceiptSink.AppendFailedReasonCode
            : reasonCode;
    }

    /// <summary>
    /// Gets the stable, sanitized reason code.
    /// </summary>
    public string ReasonCode { get; }
}
