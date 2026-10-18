namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Describes the governance decision evidence persisted for the current request.
/// </summary>
/// <param name="DecisionAuditRecordId">
/// The AsiBackbone audit ledger record identifier. NCAT mutation-audit records carry it as their decision audit record
/// identifier, which links an application change to the decision that permitted it.
/// </param>
/// <param name="DecisionReceiptId">The AsiBackbone decision receipt identifier, when the receipt supplied one.</param>
/// <param name="OperationName">The governed endpoint operation name.</param>
/// <param name="Outcome">The persisted governance decision outcome.</param>
/// <param name="OperationExecutionId">
/// The logical execution identifier assigned to this governed request. NCAT mutation audit and the AsiBackbone
/// audit-completion lifecycle event both carry it.
/// </param>
/// <param name="CorrelationId">The correlation identifier recorded on the decision receipt.</param>
/// <param name="TraceId">The trace identifier recorded on the decision receipt.</param>
public sealed record GovernanceDecisionEvidence(
    string DecisionAuditRecordId,
    string? DecisionReceiptId,
    string OperationName,
    string Outcome,
    string OperationExecutionId,
    string? CorrelationId,
    string? TraceId);
