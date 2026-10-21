using ReferenceApplication.Web.Governance.Simulation;

namespace ReferenceApplication.Web.Governance.Replay;

/// <summary>
/// Stable status values for a replay input: where its value came from.
/// </summary>
public static class DecisionReplayInputStatuses
{
    /// <summary>The value is stored with the decision.</summary>
    public const string Recorded = "recorded";

    /// <summary>The value is not stored, but the recorded outcome and reason codes determine it.</summary>
    public const string Inferred = "inferred";

    /// <summary>The value is not stored and cannot be determined from what is.</summary>
    public const string Unrecoverable = "unrecoverable";
}

/// <summary>
/// Stable keys of the actor facts a replay may have to branch on.
/// </summary>
public static class DecisionReplayFactKeys
{
    /// <summary>Whether the actor held a configured administrator role.</summary>
    public const string Administrator = "administrator";

    /// <summary>Whether the actor held a valid consequential-execution capability grant.</summary>
    public const string CapabilityGrant = "capabilityGrant";
}

/// <summary>
/// One page of recent decisions, newest first.
/// </summary>
/// <param name="Decisions">The decisions on this page.</param>
/// <param name="NextCursor">The cursor for the next, older page, or <see langword="null" /> on the last page.</param>
public sealed record DecisionReplayPage(IReadOnlyList<DecisionReplaySummary> Decisions, string? NextCursor);

/// <summary>
/// A persisted decision in the recent-decisions list.
/// </summary>
/// <param name="DecisionReceiptId">The decision receipt identifier.</param>
/// <param name="RecordId">The ledger record identifier, which is also the list cursor.</param>
/// <param name="OccurredUtc">When the decision occurred.</param>
/// <param name="OperationName">The governed operation name.</param>
/// <param name="Outcome">The recorded outcome.</param>
/// <param name="ReasonCodes">The recorded reason codes.</param>
/// <param name="ActorId">The recorded actor identifier.</param>
/// <param name="PolicyMatchesCurrent">Whether the recorded policy hash is the current one.</param>
public sealed record DecisionReplaySummary(
    string DecisionReceiptId,
    string RecordId,
    DateTimeOffset OccurredUtc,
    string OperationName,
    string Outcome,
    IReadOnlyList<string> ReasonCodes,
    string ActorId,
    bool PolicyMatchesCurrent);

/// <summary>
/// A persisted decision and what can be reconstructed from it.
/// </summary>
/// <param name="DecisionReceiptId">The decision receipt identifier.</param>
/// <param name="RecordId">The ledger record identifier.</param>
/// <param name="OccurredUtc">When the decision occurred.</param>
/// <param name="OperationName">The governed operation name.</param>
/// <param name="OperationKey">The sample operation the decision was made for, when it is still mapped.</param>
/// <param name="ActorId">The recorded actor identifier.</param>
/// <param name="ActorType">The recorded actor type.</param>
/// <param name="ActorDisplayName">The recorded actor display name.</param>
/// <param name="Outcome">The recorded outcome.</param>
/// <param name="ReasonCodes">The recorded reason codes.</param>
/// <param name="CorrelationId">The recorded correlation identifier.</param>
/// <param name="CapabilityGrantId">The capability grant the receipt names, when it names one.</param>
/// <param name="Policy">The recorded policy compared with the current one.</param>
/// <param name="Evidence">The stored evidence hash and its verification.</param>
/// <param name="Inputs">Each evaluation input, and whether it was recorded, inferred, or is unrecoverable.</param>
/// <param name="Replayable">Whether the decision can be replayed.</param>
/// <param name="NotReplayableReason">Why it cannot, when it cannot.</param>
public sealed record DecisionReplayDetails(
    string DecisionReceiptId,
    string RecordId,
    DateTimeOffset OccurredUtc,
    string OperationName,
    string? OperationKey,
    string ActorId,
    string ActorType,
    string? ActorDisplayName,
    string Outcome,
    IReadOnlyList<string> ReasonCodes,
    string? CorrelationId,
    string? CapabilityGrantId,
    DecisionReplayPolicyComparison Policy,
    DecisionReplayEvidence Evidence,
    IReadOnlyList<DecisionReplayInput> Inputs,
    bool Replayable,
    string? NotReplayableReason);

/// <summary>
/// The policy a decision was recorded under, compared with the current one.
/// </summary>
/// <param name="RecordedVersion">The recorded policy version.</param>
/// <param name="RecordedHash">The recorded policy hash.</param>
/// <param name="CurrentVersion">The current policy version.</param>
/// <param name="CurrentHash">The current policy hash.</param>
/// <param name="Matches">Whether the recorded hash is the current one.</param>
/// <param name="EndpointDeclarationsChanged">Whether the endpoint's governance declarations differ from the recorded ones.</param>
/// <param name="Notices">What a reader should know before replaying.</param>
public sealed record DecisionReplayPolicyComparison(
    string? RecordedVersion,
    string? RecordedHash,
    string CurrentVersion,
    string CurrentHash,
    bool Matches,
    bool EndpointDeclarationsChanged,
    IReadOnlyList<string> Notices);

/// <summary>
/// The stored evidence hash of a decision and the result of recomputing it.
/// </summary>
/// <param name="Status">One of the <see cref="GovernanceEvidenceVerificationStatuses" /> values.</param>
/// <param name="StoredHash">The stored hash, when present.</param>
/// <param name="ComputedHash">The recomputed hash, when verification was possible.</param>
/// <param name="HashAlgorithm">The algorithm used to recompute it.</param>
public sealed record DecisionReplayEvidence(
    string Status,
    string? StoredHash,
    string? ComputedHash,
    string? HashAlgorithm);

/// <summary>
/// One input to the evaluation and where its value comes from.
/// </summary>
/// <param name="Key">A stable key for the input.</param>
/// <param name="Name">The input, as a reader would name it.</param>
/// <param name="Status">One of the <see cref="DecisionReplayInputStatuses" /> values.</param>
/// <param name="Value">The value, when it is known.</param>
/// <param name="Explanation">How the value was obtained, or why it cannot be.</param>
public sealed record DecisionReplayInput(
    string Key,
    string Name,
    string Status,
    string? Value,
    string Explanation);

/// <summary>
/// Describes what to replay a decision against, besides the current policy.
/// </summary>
/// <param name="DisabledConstraints">The registered constraints to leave out of the candidate policy.</param>
/// <param name="UseCandidatePolicy">Whether the candidate is the stand-in next policy version.</param>
public sealed record DecisionReplayRequest(IReadOnlyCollection<string> DisabledConstraints, bool UseCandidatePolicy);

/// <summary>
/// Carries a replay result, or why there is none.
/// </summary>
/// <param name="Result">The result, when the decision was replayed.</param>
/// <param name="Error">Why the request could not be replayed, when it was invalid.</param>
/// <param name="NotFound">Whether no decision has the receipt identifier.</param>
public sealed record DecisionReplayOutcome(DecisionReplayResult? Result, string? Error, bool NotFound)
{
    /// <summary>Gets the outcome for an unknown receipt identifier.</summary>
    public static DecisionReplayOutcome Missing { get; } = new(Result: null, Error: null, NotFound: true);

    /// <summary>
    /// Creates the outcome for a request that cannot be replayed.
    /// </summary>
    /// <param name="error">The reason.</param>
    /// <returns>The failed outcome.</returns>
    public static DecisionReplayOutcome Invalid(string error)
    {
        return new DecisionReplayOutcome(Result: null, Error: error, NotFound: false);
    }
}

/// <summary>
/// A decision replayed against the current policy and, optionally, a candidate policy. Nothing in it was persisted.
/// </summary>
/// <param name="Decision">The persisted decision.</param>
/// <param name="Current">The replay against the current policy.</param>
/// <param name="Candidate">The what-if replay against the candidate policy, when one was asked for.</param>
public sealed record DecisionReplayResult(
    DecisionReplayDetails Decision,
    DecisionReplayEvaluation Current,
    DecisionReplayEvaluation? Candidate);

/// <summary>
/// A decision replayed against one policy.
/// </summary>
/// <param name="Label">What the column shows, such as "Current policy".</param>
/// <param name="Hypothetical">Whether this is a what-if rather than a re-verification of the recorded decision.</param>
/// <param name="HypotheticalReason">Why it is a what-if, when it is.</param>
/// <param name="PolicyVersion">The policy version replayed under.</param>
/// <param name="PolicyHash">The policy hash replayed under.</param>
/// <param name="DisabledConstraints">The registered constraints left out.</param>
/// <param name="PolicyChanges">What the candidate policy changes, when it is the stand-in next version.</param>
/// <param name="Partial">Whether the result depends on an input the ledger does not record.</param>
/// <param name="DecisiveUnknownInputs">The unrecorded inputs the result depends on.</param>
/// <param name="IgnoredUnknownInputs">The unrecorded inputs that made no difference to the result.</param>
/// <param name="Branches">One branch when the result is determined; one per assumption when it is partial.</param>
public sealed record DecisionReplayEvaluation(
    string Label,
    bool Hypothetical,
    string? HypotheticalReason,
    string PolicyVersion,
    string PolicyHash,
    IReadOnlyList<string> DisabledConstraints,
    IReadOnlyList<string> PolicyChanges,
    bool Partial,
    IReadOnlyList<string> DecisiveUnknownInputs,
    IReadOnlyList<string> IgnoredUnknownInputs,
    IReadOnlyList<DecisionReplayBranch> Branches);

/// <summary>
/// The replayed decision under one set of assumptions about unrecorded inputs.
/// </summary>
/// <param name="Assumptions">The values assumed for unrecorded inputs; empty when none mattered.</param>
/// <param name="Outcome">The replayed outcome.</param>
/// <param name="ReasonCodes">The replayed reason codes.</param>
/// <param name="Difference">How the replayed decision differs from the recorded one.</param>
/// <param name="Constraints">Each constraint's result, in evaluation order.</param>
/// <param name="DecisionPolicy">The composed decision and the decision-policy step.</param>
/// <param name="Capability">The capability-grant step.</param>
/// <param name="Receipt">The receipt the replayed decision would have produced. It was not appended.</param>
/// <param name="EndpointResult">What the endpoint would do with the replayed decision.</param>
public sealed record DecisionReplayBranch(
    IReadOnlyDictionary<string, bool> Assumptions,
    string Outcome,
    IReadOnlyList<string> ReasonCodes,
    DecisionReplayDifference Difference,
    IReadOnlyList<PolicySimulationConstraintStage> Constraints,
    PolicySimulationDecisionPolicyStage DecisionPolicy,
    PolicySimulationCapabilityStage Capability,
    PolicySimulationReceipt? Receipt,
    PolicySimulationEndpointResult EndpointResult);

/// <summary>
/// How a replayed decision differs from the recorded one.
/// </summary>
/// <param name="Matches">Whether the outcome and reason codes are the same.</param>
/// <param name="OutcomeChanged">Whether the outcome differs.</param>
/// <param name="AddedReasonCodes">Reason codes the replay has that the record does not.</param>
/// <param name="RemovedReasonCodes">Reason codes the record has that the replay does not.</param>
public sealed record DecisionReplayDifference(
    bool Matches,
    bool OutcomeChanged,
    IReadOnlyList<string> AddedReasonCodes,
    IReadOnlyList<string> RemovedReasonCodes);
