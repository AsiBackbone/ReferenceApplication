namespace ReferenceApplication.Web.Governance.FailureScenarios;

/// <summary>
/// A scenario as the explorer lists it, with whether the current actor can run it.
/// </summary>
/// <param name="Id">The stable scenario identifier.</param>
/// <param name="Number">The scenario's number in the walkthrough.</param>
/// <param name="Title">The short title.</param>
/// <param name="Path">The governed endpoint the scenario calls, or <see langword="null" /> when it sends no request.</param>
/// <param name="Teaches">What the scenario shows.</param>
/// <param name="Expectation">The outcome and evidence the scenario expects.</param>
/// <param name="Available">Whether the scenario can run now.</param>
/// <param name="UnavailableReason">Why the scenario cannot run now, when it cannot.</param>
public sealed record FailureScenarioView(
    string Id,
    int Number,
    string Title,
    string? Path,
    string Teaches,
    string Expectation,
    bool Available,
    string? UnavailableReason);

/// <summary>
/// The result of asking to run a scenario.
/// </summary>
/// <param name="Run">The armed run, for a scenario that sends a governed request.</param>
/// <param name="Tampering">The result, for the evidence-tampering scenario, which sends no request.</param>
/// <param name="ErrorStatusCode">The status code to answer with when the scenario cannot run.</param>
/// <param name="ErrorMessage">Why the scenario cannot run.</param>
public sealed record FailureScenarioStartResult(
    FailureScenarioRunState? Run,
    FailureScenarioTamperingReport? Tampering,
    int? ErrorStatusCode,
    string? ErrorMessage)
{
    /// <summary>Creates a result for an armed run.</summary>
    /// <param name="run">The armed run.</param>
    /// <returns>The result.</returns>
    public static FailureScenarioStartResult Armed(FailureScenarioRunState run)
    {
        return new(run, null, null, null);
    }

    /// <summary>Creates a result for a completed tampering check.</summary>
    /// <param name="report">The tampering report.</param>
    /// <returns>The result.</returns>
    public static FailureScenarioStartResult Tampered(FailureScenarioTamperingReport report)
    {
        return new(null, report, null, null);
    }

    /// <summary>Creates a result for a scenario that cannot run.</summary>
    /// <param name="statusCode">The status code to answer with.</param>
    /// <param name="message">Why the scenario cannot run.</param>
    /// <returns>The result.</returns>
    public static FailureScenarioStartResult Refused(int statusCode, string message)
    {
        return new(null, null, statusCode, message);
    }
}

/// <summary>
/// What a run's request did and what evidence it left behind.
/// </summary>
/// <param name="Scenario">The scenario.</param>
/// <param name="Run">The run's state.</param>
/// <param name="ActionBody">
/// Whether the governed action body ran: <c>not-run</c>, <c>ran</c>, or <c>ran-rolled-back</c>; <see langword="null" />
/// until the request ends.
/// </param>
/// <param name="Decisions">The decisions the ledger holds for the request.</param>
/// <param name="MutationAuditRecordCount">The NCAT mutation-audit records the request committed.</param>
/// <param name="Outbox">The request's audit-completion outbox entry, when it staged one.</param>
/// <param name="Reconciliation">What reconciliation reports for the request, for the execution-failure scenario.</param>
/// <param name="Timeline">The request's evidence timeline, as the explorer's evidence view shows it.</param>
public sealed record FailureScenarioRunReport(
    FailureScenarioView Scenario,
    FailureScenarioRunState Run,
    string? ActionBody,
    IReadOnlyList<FailureScenarioDecision> Decisions,
    int MutationAuditRecordCount,
    FailureScenarioOutboxEntry? Outbox,
    FailureScenarioReconciliation? Reconciliation,
    GovernanceEvidenceTimelineResponse? Timeline);

/// <summary>
/// A decision the ledger holds for a run's request.
/// </summary>
/// <param name="RecordId">The ledger record identifier.</param>
/// <param name="OperationName">The governed operation.</param>
/// <param name="Outcome">The decision outcome.</param>
/// <param name="ReasonCodes">The decision's reason codes.</param>
/// <param name="CapabilityTokenId">The capability token that authorized the decision, when one did.</param>
/// <param name="RecordedUtc">When the record was appended.</param>
public sealed record FailureScenarioDecision(
    string RecordId,
    string OperationName,
    string Outcome,
    IReadOnlyList<string> ReasonCodes,
    string? CapabilityTokenId,
    DateTimeOffset RecordedUtc);

/// <summary>
/// A run's audit-completion outbox entry.
/// </summary>
/// <param name="Status">The outbox status.</param>
/// <param name="RetryCount">The failed delivery attempts so far.</param>
/// <param name="MaxRetryAttempts">The failed attempts the outbox allows before it dead-letters the entry.</param>
/// <param name="LastAttemptUtc">When delivery was last attempted.</param>
/// <param name="NextAttemptUtc">When delivery is next attempted.</param>
/// <param name="DeliveredUtc">When the entry was delivered.</param>
/// <param name="LastErrorCode">The error code of the last failed attempt.</param>
/// <param name="LastErrorMessage">The error message of the last failed attempt.</param>
/// <param name="LifecycleEventAppended">Whether the lifecycle event the delivery appends exists.</param>
public sealed record FailureScenarioOutboxEntry(
    string Status,
    int RetryCount,
    int MaxRetryAttempts,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? NextAttemptUtc,
    DateTimeOffset? DeliveredUtc,
    string? LastErrorCode,
    string? LastErrorMessage,
    bool LifecycleEventAppended);

/// <summary>
/// What NCAT audit reconciliation reports about a run's request.
/// </summary>
/// <param name="Registered">Whether reconciliation is registered in this host.</param>
/// <param name="LastRunUtc">When reconciliation last ran.</param>
/// <param name="OpenFindingCount">The open findings across the database.</param>
/// <param name="MutationBatchIds">The mutation batches the request committed, which findings are keyed by.</param>
/// <param name="FindingReasonCodes">The reason codes of findings about those batches.</param>
/// <param name="Explanation">What the result means for this request.</param>
public sealed record FailureScenarioReconciliation(
    bool Registered,
    DateTimeOffset? LastRunUtc,
    long OpenFindingCount,
    IReadOnlyList<string> MutationBatchIds,
    IReadOnlyList<string> FindingReasonCodes,
    string Explanation);

/// <summary>
/// The result of verifying a changed in-memory copy of a persisted decision record.
/// </summary>
/// <param name="CorrelationId">The correlation identifier of the decision.</param>
/// <param name="RecordId">The ledger record identifier.</param>
/// <param name="Field">The field changed on the copy.</param>
/// <param name="StoredValue">The value stored in the ledger.</param>
/// <param name="TamperedValue">The value written to the copy.</param>
/// <param name="UnchangedCopy">The verification of a copy made the same way without the change.</param>
/// <param name="TamperedCopy">The verification of the changed copy.</param>
/// <param name="StoredRecord">The verification of the stored record, read again after the check.</param>
public sealed record FailureScenarioTamperingReport(
    string CorrelationId,
    string RecordId,
    string Field,
    string StoredValue,
    string TamperedValue,
    GovernanceEvidenceRecordVerification UnchangedCopy,
    GovernanceEvidenceRecordVerification TamperedCopy,
    GovernanceEvidenceRecordVerification StoredRecord);
