namespace ReferenceApplication.Web.Governance.FailureScenarios;

/// <summary>
/// Defines the preset failure and attacker scenarios the explorer offers.
/// </summary>
public static class FailureScenarioCatalog
{
    /// <summary>The capability-protected endpoint is called without the capability grant.</summary>
    public const string MissingCapabilityGrant = "missing-capability-grant";

    /// <summary>A Development-only demonstration constraint throws during policy evaluation.</summary>
    public const string ConstraintThrows = "constraint-throws";

    /// <summary>The decision receipt cannot be appended to the audit ledger.</summary>
    public const string LedgerAppendFails = "ledger-append-fails";

    /// <summary>The outbox cannot append the lifecycle event for a delivered audit completion.</summary>
    public const string OutboxDeliveryFails = "outbox-delivery-fails";

    /// <summary>The decision is allowed and persisted, then the mutation fails.</summary>
    public const string ExecutionFailsAfterApproval = "execution-fails-after-approval";

    /// <summary>A field of an in-memory copy of a persisted decision record is changed and the copy is verified.</summary>
    public const string EvidenceTampering = "evidence-tampering";

    /// <summary>A request whose payload differs from the payload bound into an otherwise valid token.</summary>
    public const string PayloadTampering = "payload-tampering";

    /// <summary>A token presented after it expired.</summary>
    public const string ExpiredToken = "expired-token";

    /// <summary>The path of the capability-protected governed endpoint.</summary>
    public const string CapabilityPath = "/api/v1/governed-operations/capability";

    /// <summary>The path of the governed endpoint that creates an audited record.</summary>
    public const string RecordsPath = "/api/v1/governed-operations/records";

    /// <summary>
    /// Gets the scenarios, in the order the explorer lists them.
    /// </summary>
    public static IReadOnlyList<FailureScenarioDefinition> Scenarios { get; } =
    [
        new(
            MissingCapabilityGrant,
            1,
            "Missing capability grant",
            CapabilityPath,
            "The capability check runs for every decision that is not already denied, so an actor without the grant is " +
            "denied before the action runs, and the denial is recorded.",
            "HTTP 403. The decision is denied for the missing grant and recorded in the ledger. The action does not run.",
            RequiresCapabilityTokens: false),
        new(
            ConstraintThrows,
            2,
            "Constraint throws",
            RecordsPath,
            "AsiBackbone's evaluator treats a constraint that throws as a denial, so a broken rule fails closed instead of " +
            "letting the request through.",
            "HTTP 403. The decision is denied with AsiBackbone's constraint-exception reason code. No record is created.",
            RequiresCapabilityTokens: false),
        new(
            LedgerAppendFails,
            3,
            "Ledger append fails",
            RecordsPath,
            "A governed action never runs without its decision evidence: when the receipt cannot be appended, the request " +
            "fails before the action body.",
            "HTTP 500. The sink throws GovernanceEvidencePersistenceException with the ledger's reason code. No ledger " +
            "row and no record are written.",
            RequiresCapabilityTokens: false),
        new(
            OutboxDeliveryFails,
            4,
            "Outbox delivery fails",
            RecordsPath,
            "The request path is unaffected by a failing destination: the record and its outbox entry commit, and " +
            "delivery retries in the background until the fault is cleared.",
            "HTTP 201. The outbox entry is retried with a backoff schedule and is delivered, with its lifecycle event, " +
            "once the fault is cleared.",
            RequiresCapabilityTokens: false),
        new(
            ExecutionFailsAfterApproval,
            5,
            "Execution fails after approval",
            RecordsPath,
            "A decision records permission, not execution. When the mutation fails, the allowed decision stays in the " +
            "ledger with no mutation audit, outbox entry, or lifecycle event to show the action completed.",
            "HTTP 500. The ledger holds an allowed decision; the mutation and its audit roll back together, and no outbox " +
            "entry is staged.",
            RequiresCapabilityTokens: false),
        new(
            EvidenceTampering,
            6,
            "Evidence tampering",
            Path: null,
            "Each ledger record carries a hash of its canonical payload, so changing any field is detected when the " +
            "record is verified.",
            "The unchanged copy verifies; the copy with one changed field reports a hash mismatch. The stored row is " +
            "never modified.",
            RequiresCapabilityTokens: false),
        new(
            PayloadTampering,
            7,
            "Payload tampering under a valid token",
            CapabilityPath,
            "A signed capability token is bound to the request payload it was issued for, so a valid token cannot " +
            "authorize a different payload.",
            "HTTP 403. AsiBackbone denies the request for the resource-binding mismatch. The action does not run.",
            RequiresCapabilityTokens: true),
        new(
            ExpiredToken,
            8,
            "Expired token replay",
            CapabilityPath,
            "A token is honored only inside its validity window. The scenario issues the token from a clock set back past " +
            "its lifetime, rather than waiting for it to expire.",
            "HTTP 403. AsiBackbone denies the request because the token has expired. The action does not run.",
            RequiresCapabilityTokens: true)
    ];

    /// <summary>
    /// Finds a scenario by identifier.
    /// </summary>
    /// <param name="scenarioId">The scenario identifier.</param>
    /// <returns>The scenario, or <see langword="null" /> when none has that identifier.</returns>
    public static FailureScenarioDefinition? Find(string? scenarioId)
    {
        return Scenarios.FirstOrDefault(scenario => string.Equals(scenario.Id, scenarioId?.Trim(), StringComparison.Ordinal));
    }
}

/// <summary>
/// Describes one preset failure or attacker scenario.
/// </summary>
/// <param name="Id">The stable scenario identifier.</param>
/// <param name="Number">The scenario's number in the issue and the walkthrough.</param>
/// <param name="Title">The short title.</param>
/// <param name="Path">The governed endpoint the scenario calls, or <see langword="null" /> when it sends no request.</param>
/// <param name="Teaches">What the scenario shows.</param>
/// <param name="Expectation">The outcome and evidence the scenario expects.</param>
/// <param name="RequiresCapabilityTokens">Whether the scenario needs signed capability tokens to be enabled.</param>
public sealed record FailureScenarioDefinition(
    string Id,
    int Number,
    string Title,
    string? Path,
    string Teaches,
    string Expectation,
    bool RequiresCapabilityTokens);
