namespace ReferenceApplication.Web.Governance.Simulation;

/// <summary>
/// Describes one policy simulation: the governed operation, the simulated actor, and the policy to apply.
/// </summary>
/// <param name="Operation">The governed operation key: <c>administrator</c>, <c>capability</c>, <c>consequential</c>, or <c>records</c>.</param>
/// <param name="Administrator">Whether the simulated actor holds a configured administrator role.</param>
/// <param name="ConsequentialCapability">Whether the simulated actor holds the consequential-execution capability claim.</param>
/// <param name="Acknowledged">Whether the acknowledgment flag is set, which the explorer requires before it sends the consequential request.</param>
/// <param name="Payload">The sample payload value: the record title for <c>records</c>, a note for the other operations.</param>
/// <param name="DisabledConstraints">The names of the constraints to leave out of the simulated policy.</param>
public sealed record PolicySimulationRequest(
    string Operation,
    bool Administrator,
    bool ConsequentialCapability,
    bool Acknowledged,
    string? Payload,
    IReadOnlyCollection<string> DisabledConstraints);

/// <summary>
/// Carries either a simulation result or the reason the request could not be simulated.
/// </summary>
/// <param name="Result">The simulation result, when the request was valid.</param>
/// <param name="Error">The reason the request could not be simulated, when it was not.</param>
public sealed record PolicySimulationOutcome(PolicySimulationResult? Result, string? Error)
{
    /// <summary>
    /// Creates an outcome for a request that could not be simulated.
    /// </summary>
    /// <param name="error">The reason.</param>
    /// <returns>The failed outcome.</returns>
    public static PolicySimulationOutcome Invalid(string error)
    {
        return new PolicySimulationOutcome(Result: null, Error: error);
    }
}

/// <summary>
/// The stage-by-stage result of a simulated governance evaluation. Nothing in it was persisted.
/// </summary>
/// <param name="Operation">The governed operation key.</param>
/// <param name="OperationName">The operation name, as endpoint governance reports it.</param>
/// <param name="SimulatedWithModifiedPolicy">Whether one or more constraints were left out, so the result differs from the real policy.</param>
/// <param name="DisabledConstraints">The constraints left out of the simulated policy.</param>
/// <param name="Actor">The simulated actor.</param>
/// <param name="Payload">The form fields the governed request would post, excluding the antiforgery field.</param>
/// <param name="Constraints">Each constraint's result, in evaluation order.</param>
/// <param name="DecisionPolicy">The composed decision and the decision-policy step.</param>
/// <param name="Capability">The capability-grant validation step.</param>
/// <param name="Receipt">The decision receipt that would be appended to the ledger.</param>
/// <param name="EndpointResult">What the real endpoint would do with the decision.</param>
/// <param name="SendForReal">Whether the same inputs can be sent through the real endpoint from the explorer.</param>
public sealed record PolicySimulationResult(
    string Operation,
    string OperationName,
    bool SimulatedWithModifiedPolicy,
    IReadOnlyList<string> DisabledConstraints,
    PolicySimulationActor Actor,
    IReadOnlyDictionary<string, string> Payload,
    IReadOnlyList<PolicySimulationConstraintStage> Constraints,
    PolicySimulationDecisionPolicyStage DecisionPolicy,
    PolicySimulationCapabilityStage Capability,
    PolicySimulationReceipt? Receipt,
    PolicySimulationEndpointResult EndpointResult,
    PolicySimulationSendForReal SendForReal);

/// <summary>
/// The simulated actor.
/// </summary>
/// <param name="ActorId">The governance actor identifier, taken from the signed-in actor.</param>
/// <param name="DisplayName">The actor display name, when known.</param>
/// <param name="Administrator">Whether the simulated actor holds a configured administrator role.</param>
/// <param name="ConsequentialCapability">Whether the simulated actor holds the consequential-execution capability claim.</param>
/// <param name="MatchesSignedInActor">Whether the simulated role and capability match the signed-in actor's.</param>
public sealed record PolicySimulationActor(
    string ActorId,
    string? DisplayName,
    bool Administrator,
    bool ConsequentialCapability,
    bool MatchesSignedInActor);

/// <summary>
/// One constraint's part in the simulated evaluation.
/// </summary>
/// <param name="Order">The constraint's position in evaluation order, starting at 1.</param>
/// <param name="Name">The constraint name.</param>
/// <param name="Disabled">Whether the constraint was left out of the simulated policy.</param>
/// <param name="Evaluated">Whether the evaluator ran the constraint.</param>
/// <param name="Outcome">The constraint outcome, when it ran.</param>
/// <param name="ReasonCodes">The constraint's reason codes.</param>
public sealed record PolicySimulationConstraintStage(
    int Order,
    string Name,
    bool Disabled,
    bool Evaluated,
    string? Outcome,
    IReadOnlyList<string> ReasonCodes);

/// <summary>
/// The composed decision and the decision-policy step applied to it.
/// </summary>
/// <param name="Evaluated">Whether the evaluator ran, which it does whenever the endpoint declares a policy.</param>
/// <param name="ComposedOutcome">The decision composed from the constraint results.</param>
/// <param name="ComposedReasonCodes">The composed decision's reason codes.</param>
/// <param name="Outcome">The decision after the decision policy.</param>
/// <param name="ReasonCodes">The final decision's reason codes.</param>
/// <param name="Changed">Whether the decision policy changed the composed decision.</param>
public sealed record PolicySimulationDecisionPolicyStage(
    bool Evaluated,
    string? ComposedOutcome,
    IReadOnlyList<string> ComposedReasonCodes,
    string? Outcome,
    IReadOnlyList<string> ReasonCodes,
    bool Changed);

/// <summary>
/// The capability-grant validation step.
/// </summary>
/// <param name="Applies">Whether the endpoint declares capability scopes.</param>
/// <param name="Model">The active grant model: <c>claims</c> or <c>signed-token</c>.</param>
/// <param name="Evaluated">Whether validation ran; it is skipped for a decision that is already denied.</param>
/// <param name="RequiredScopes">The capability scopes the endpoint declares.</param>
/// <param name="InputOutcome">The decision before validation.</param>
/// <param name="Outcome">The decision after validation.</param>
/// <param name="ReasonCodes">The decision's reason codes after validation.</param>
/// <param name="Token">The token that would be issued, when the signed-token model is active.</param>
public sealed record PolicySimulationCapabilityStage(
    bool Applies,
    string Model,
    bool Evaluated,
    IReadOnlyList<string> RequiredScopes,
    string? InputOutcome,
    string? Outcome,
    IReadOnlyList<string> ReasonCodes,
    PolicySimulationTokenPreview? Token);

/// <summary>
/// The capability token issuance would sign for the simulated request. It is never signed and consumes no use.
/// </summary>
/// <param name="WouldBeIssued">Whether the simulated actor is entitled to the token.</param>
/// <param name="TokenId">The token identifier the receipt would record.</param>
/// <param name="Issuer">The token issuer.</param>
/// <param name="Audience">The token audience.</param>
/// <param name="SubjectId">The actor the token is bound to.</param>
/// <param name="OperationName">The operation the token is bound to.</param>
/// <param name="Scopes">The granted capability scopes.</param>
/// <param name="ResourceBinding">The hash of the request payload the token is bound to.</param>
/// <param name="IssuedUtc">When the token would be issued.</param>
/// <param name="NotBeforeUtc">When the token would become valid.</param>
/// <param name="ExpiresUtc">When the token would expire.</param>
/// <param name="MaxUseCount">How many times the token could be used.</param>
/// <param name="PolicyVersion">The policy version the token is bound to.</param>
/// <param name="PolicyHash">The policy hash the token is bound to.</param>
/// <param name="KeyId">The identifier of the key that would sign it. The key itself is never shown.</param>
/// <param name="KeyVersion">The version of the key that would sign it.</param>
/// <param name="SignatureAlgorithm">The signature algorithm descriptor the proof would carry.</param>
public sealed record PolicySimulationTokenPreview(
    bool WouldBeIssued,
    string? TokenId,
    string? Issuer,
    string? Audience,
    string? SubjectId,
    string? OperationName,
    IReadOnlyList<string> Scopes,
    string? ResourceBinding,
    DateTimeOffset? IssuedUtc,
    DateTimeOffset? NotBeforeUtc,
    DateTimeOffset? ExpiresUtc,
    int? MaxUseCount,
    string? PolicyVersion,
    string? PolicyHash,
    string? KeyId,
    string? KeyVersion,
    string? SignatureAlgorithm);

/// <summary>
/// The decision receipt that would be appended to the ledger.
/// </summary>
/// <param name="DecisionReceiptId">The receipt identifier.</param>
/// <param name="EventId">The receipt event identifier.</param>
/// <param name="SchemaVersion">The receipt schema version.</param>
/// <param name="OccurredUtc">When the decision occurred.</param>
/// <param name="ActorId">The governance actor identifier.</param>
/// <param name="ActorType">The governance actor type.</param>
/// <param name="ActorDisplayName">The actor display name, when known.</param>
/// <param name="OperationName">The governed operation name.</param>
/// <param name="Outcome">The decision outcome.</param>
/// <param name="ReasonCodes">The decision reason codes.</param>
/// <param name="CorrelationId">The simulated request's correlation identifier.</param>
/// <param name="DecisionStage">The governance stage that produced the receipt.</param>
/// <param name="PolicyVersion">The policy version.</param>
/// <param name="PolicyHash">The policy hash.</param>
/// <param name="CapabilityGrantId">The capability grant that authorized the decision, when one did.</param>
/// <param name="Metadata">The receipt metadata.</param>
public sealed record PolicySimulationReceipt(
    string DecisionReceiptId,
    string EventId,
    string SchemaVersion,
    DateTimeOffset OccurredUtc,
    string ActorId,
    string ActorType,
    string? ActorDisplayName,
    string OperationName,
    string Outcome,
    IReadOnlyList<string> ReasonCodes,
    string? CorrelationId,
    string? DecisionStage,
    string? PolicyVersion,
    string? PolicyHash,
    string? CapabilityGrantId,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>
/// What the real endpoint would do with the decision.
/// </summary>
/// <param name="Kind">The result: <c>allowed</c>, <c>blocked</c>, or <c>acknowledgment-challenge</c>.</param>
/// <param name="StatusCode">The HTTP status the endpoint would return, when governance answers the request itself.</param>
/// <param name="Description">A short description of the result.</param>
/// <param name="RequiredAcknowledgmentCode">The acknowledgment code the challenge would ask for.</param>
/// <param name="RequiredAcknowledgmentText">The acknowledgment text the challenge would ask for.</param>
public sealed record PolicySimulationEndpointResult(
    string Kind,
    int? StatusCode,
    string Description,
    string? RequiredAcknowledgmentCode,
    string? RequiredAcknowledgmentText);

/// <summary>
/// Whether the same inputs can be sent through the real endpoint from the explorer.
/// </summary>
/// <param name="Available">Whether sending is available.</param>
/// <param name="Reason">Why sending is unavailable, when it is.</param>
public sealed record PolicySimulationSendForReal(bool Available, string? Reason);
