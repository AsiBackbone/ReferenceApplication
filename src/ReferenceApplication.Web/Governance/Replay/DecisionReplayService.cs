using System.Security.Claims;
using System.Text.Json;
using AsiBackbone.AspNetCore.Actors;
using AsiBackbone.AspNetCore.CapabilityGrants;
using AsiBackbone.AspNetCore.Endpoints;
using AsiBackbone.Core.Actors;
using AsiBackbone.Core.Audit;
using AsiBackbone.Core.Constraints;
using AsiBackbone.Core.Decisions;
using AsiBackbone.Core.Evaluation;
using AsiBackbone.Core.Signing;
using AsiBackbone.EntityFrameworkCore.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ReferenceApplication.Infrastructure.Data;
using ReferenceApplication.Web.Authentication.Claims;
using ReferenceApplication.Web.Authentication.Options;
using ReferenceApplication.Web.Controllers.Api;
using ReferenceApplication.Web.Governance.Simulation;

namespace ReferenceApplication.Web.Governance.Replay;

/// <summary>
/// Finds persisted governance decisions and replays them against the current policy or a candidate policy.
/// </summary>
public interface IDecisionReplayService
{
    /// <summary>
    /// Gets the names of the registered constraints, in evaluation order.
    /// </summary>
    IReadOnlyList<string> ConstraintNames { get; }

    /// <summary>
    /// Lists persisted decisions, newest first.
    /// </summary>
    /// <param name="before">The record identifier the previous page ended with, or <see langword="null" /> for the newest page.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The page, or <see langword="null" /> when the cursor is not a persisted decision.</returns>
    ValueTask<DecisionReplayPage?> ListAsync(string? before, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a persisted decision by its receipt identifier.
    /// </summary>
    /// <param name="decisionReceiptId">The decision receipt identifier.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The decision and what can be reconstructed from it, or <see langword="null" /> when it is unknown.</returns>
    ValueTask<DecisionReplayDetails?> FindAsync(string decisionReceiptId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replays a persisted decision. Nothing is persisted, and stored evidence is never changed.
    /// </summary>
    /// <param name="requestContext">The request asking for the replay.</param>
    /// <param name="decisionReceiptId">The decision receipt identifier.</param>
    /// <param name="request">What to replay against besides the current policy.</param>
    /// <param name="cancellationToken">A token that cancels the replay.</param>
    /// <returns>The replay result, or why there is none.</returns>
    ValueTask<DecisionReplayOutcome> ReplayAsync(
        HttpContext requestContext,
        string decisionReceiptId,
        DecisionReplayRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Replays persisted decisions through the same endpoint governance pipeline the policy simulator uses.
/// </summary>
/// <remarks>
/// <para>
/// The ledger stores minimized evidence. It records the operation, the endpoint's governance declarations, the actor
/// identifier, the outcome and reason codes, and the policy version and hash, but not the actor's roles or permission
/// claims, the capability token, or the request payload. The two actor facts the policy reads — the administrator role
/// and the capability grant — are inferred from the recorded outcome and reason codes where those determine them.
/// </para>
/// <para>
/// When a replay needs a fact that cannot be inferred, it is evaluated under each value. If every value gives the same
/// decision, the fact did not matter. Otherwise the replay is partial and reports each branch, rather than inventing a
/// value.
/// </para>
/// <para>
/// A replay never invokes the decision receipt sink: the pipeline harness captures the receipt instead of writing it,
/// so the ledger, outbox, and lifecycle tables are unchanged.
/// </para>
/// </remarks>
internal sealed class DecisionReplayService : IDecisionReplayService
{
    /// <summary>
    /// Gets the capability stage model reported when signed-token grants are replayed from the record.
    /// </summary>
    public const string RecordedGrantModel = "recorded-grant";

    private const string _deniedOutcome = nameof(GovernanceDecisionOutcome.Denied);
    private const string _capabilityGrantReasonPrefix = "endpoint.capability_grant.";
    private const string _policyTypesKey = "endpoint.policy_types";
    private const string _capabilityScopesKey = "endpoint.capability_scopes";

    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly IGovernanceConstraint<GovernanceEvaluationContext>[] _constraints;
    private readonly IGovernanceDecisionPolicy<GovernanceEvaluationContext> _decisionPolicy;
    private readonly ClaimEndpointCapabilityGrantValidator _claimValidator;
    private readonly GovernancePipelineHarness _harness;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IOptions<ApplicationAuthorizationOptions> _authorizationOptions;
    private readonly IOptions<HttpGovernanceActorContextOptions> _actorOptions;
    private readonly IOptions<CapabilityTokenOptions> _tokenOptions;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="DecisionReplayService" /> class.
    /// </summary>
    /// <param name="dbContextFactory">The factory for short-lived, read-only database contexts.</param>
    /// <param name="constraints">The registered constraints, in evaluation order.</param>
    /// <param name="decisionPolicy">The registered decision policy.</param>
    /// <param name="claimValidator">The claim-backed capability validator.</param>
    /// <param name="harness">The pipeline harness that runs the replayed request.</param>
    /// <param name="httpContextAccessor">The accessor the stand-in constraint reads the replayed request through.</param>
    /// <param name="authorizationOptions">The application authorization options.</param>
    /// <param name="actorOptions">The governance actor claim options.</param>
    /// <param name="tokenOptions">The capability token options.</param>
    /// <param name="loggerFactory">The logger factory for the ledger store.</param>
    public DecisionReplayService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        IEnumerable<IGovernanceConstraint<GovernanceEvaluationContext>> constraints,
        IGovernanceDecisionPolicy<GovernanceEvaluationContext> decisionPolicy,
        ClaimEndpointCapabilityGrantValidator claimValidator,
        GovernancePipelineHarness harness,
        IHttpContextAccessor httpContextAccessor,
        IOptions<ApplicationAuthorizationOptions> authorizationOptions,
        IOptions<HttpGovernanceActorContextOptions> actorOptions,
        IOptions<CapabilityTokenOptions> tokenOptions,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(constraints);

        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _constraints = [.. constraints];
        _decisionPolicy = decisionPolicy ?? throw new ArgumentNullException(nameof(decisionPolicy));
        _claimValidator = claimValidator ?? throw new ArgumentNullException(nameof(claimValidator));
        _harness = harness ?? throw new ArgumentNullException(nameof(harness));
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _authorizationOptions = authorizationOptions ?? throw new ArgumentNullException(nameof(authorizationOptions));
        _actorOptions = actorOptions ?? throw new ArgumentNullException(nameof(actorOptions));
        _tokenOptions = tokenOptions ?? throw new ArgumentNullException(nameof(tokenOptions));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        ConstraintNames = [.. _constraints.Select(static constraint => constraint.Name)];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ConstraintNames { get; }

    /// <inheritdoc />
    public async ValueTask<DecisionReplayPage?> ListAsync(string? before, CancellationToken cancellationToken = default)
    {
        await using ApplicationDbContext dbContext = await _dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        // SQLite cannot order or compare DateTimeOffset values in SQL, so the keys of the disposable Development
        // database are ordered in memory and only one page of rows is then read.
        List<LedgerKey> keys = await dbContext.AsiBackboneAuditLedgerRecords
            .AsNoTracking()
            .Select(static record => new LedgerKey(record.RecordId, record.RecordedUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<LedgerKey> ordered = [.. keys
            .OrderByDescending(static key => key.RecordedUtc)
            .ThenByDescending(static key => key.RecordId, StringComparer.Ordinal)];

        int start = 0;
        if (!string.IsNullOrWhiteSpace(before))
        {
            string cursor = before.Trim();
            int index = ordered.FindIndex(key => string.Equals(key.RecordId, cursor, StringComparison.Ordinal));
            if (index < 0)
            {
                return null;
            }

            start = index + 1;
        }

        string[] pageIds = [.. ordered
            .Skip(start)
            .Take(DecisionReplayOptions.PageSize)
            .Select(static key => key.RecordId)];
        var rows = await dbContext.AsiBackboneAuditLedgerRecords
            .AsNoTracking()
            .Where(record => pageIds.Contains(record.RecordId))
            .Select(static record => new
            {
                record.RecordId,
                record.EventId,
                record.DecisionReceiptId,
                record.OccurredUtc,
                record.OperationName,
                record.Outcome,
                record.ReasonCodesJson,
                record.ActorId,
                record.PolicyHash
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<DecisionReplaySummary> decisions = [.. rows
            .OrderBy(row => Array.IndexOf(pageIds, row.RecordId))
            .Select(static row => new DecisionReplaySummary(
                DecisionReceiptId: row.DecisionReceiptId ?? row.EventId,
                RecordId: row.RecordId,
                OccurredUtc: row.OccurredUtc,
                OperationName: row.OperationName,
                Outcome: row.Outcome,
                ReasonCodes: ReadReasonCodes(row.ReasonCodesJson),
                ActorId: row.ActorId,
                PolicyMatchesCurrent: string.Equals(
                    row.PolicyHash,
                    ReferenceApplicationGovernancePolicySet.PolicyHash,
                    StringComparison.Ordinal)))];

        string? nextCursor = start + pageIds.Length < ordered.Count && pageIds.Length > 0 ? pageIds[^1] : null;
        return new DecisionReplayPage(decisions, nextCursor);
    }

    /// <inheritdoc />
    public async ValueTask<DecisionReplayDetails?> FindAsync(
        string decisionReceiptId,
        CancellationToken cancellationToken = default)
    {
        AuditLedgerRecord? record = await FindRecordAsync(decisionReceiptId, cancellationToken).ConfigureAwait(false);
        return record is null ? null : Reconstruct(record).Details;
    }

    /// <inheritdoc />
    public async ValueTask<DecisionReplayOutcome> ReplayAsync(
        HttpContext requestContext,
        string decisionReceiptId,
        DecisionReplayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestContext);
        ArgumentNullException.ThrowIfNull(request);

        AuditLedgerRecord? record = await FindRecordAsync(decisionReceiptId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return DecisionReplayOutcome.Missing;
        }

        Reconstruction reconstruction = Reconstruct(record);
        DecisionReplayDetails details = reconstruction.Details;
        if (!details.Replayable || reconstruction.Endpoint is null)
        {
            return DecisionReplayOutcome.Invalid(details.NotReplayableReason ?? "This decision cannot be replayed.");
        }

        HashSet<string> disabled = new(StringComparer.Ordinal);
        foreach (string name in request.DisabledConstraints)
        {
            string trimmed = name.Trim();
            if (!ConstraintNames.Contains(trimmed, StringComparer.Ordinal))
            {
                return DecisionReplayOutcome.Invalid($"'{trimmed}' is not a registered constraint.");
            }

            _ = disabled.Add(trimmed);
        }

        DecisionReplayEvaluation current = await EvaluateAsync(
            requestContext,
            record,
            reconstruction,
            new ReplayPolicy(
                Label: "Current policy",
                Constraints: _constraints,
                Disabled: new HashSet<string>(StringComparer.Ordinal),
                DecisionPolicy: _decisionPolicy,
                PolicyVersion: null,
                PolicyHash: null,
                Changes: [],
                HypotheticalReason: details.Policy.Matches
                    ? null
                    : "The decision was recorded under a different policy, so this is a what-if, not a re-verification."),
            cancellationToken).ConfigureAwait(false);

        DecisionReplayEvaluation? candidate = null;
        if (disabled.Count > 0 || request.UseCandidatePolicy)
        {
            IGovernanceConstraint<GovernanceEvaluationContext>[] candidateConstraints = request.UseCandidatePolicy
                ? [.. _constraints, new CandidateCapabilityAdministratorConstraint(_httpContextAccessor, _authorizationOptions)]
                : _constraints;
            IGovernanceDecisionPolicy<GovernanceEvaluationContext> candidateDecisionPolicy = request.UseCandidatePolicy
                ? new CandidateDecisionPolicy(_decisionPolicy, FindAuditedRecordOperationName())
                : _decisionPolicy;

            candidate = await EvaluateAsync(
                requestContext,
                record,
                reconstruction,
                new ReplayPolicy(
                    Label: request.UseCandidatePolicy
                        ? $"Candidate policy {CandidateGovernancePolicySet.Version}"
                        : "Candidate policy",
                    Constraints: candidateConstraints,
                    Disabled: disabled,
                    DecisionPolicy: candidateDecisionPolicy,
                    PolicyVersion: request.UseCandidatePolicy ? CandidateGovernancePolicySet.Version : null,
                    PolicyHash: request.UseCandidatePolicy ? CandidateGovernancePolicySet.PolicyHash : null,
                    Changes: request.UseCandidatePolicy ? CandidateGovernancePolicySet.Changes : [],
                    HypotheticalReason: "A what-if: the governed endpoints do not evaluate this policy."),
                cancellationToken).ConfigureAwait(false);
        }

        return new DecisionReplayOutcome(
            new DecisionReplayResult(details, current, candidate),
            Error: null,
            NotFound: false);
    }

    private async Task<DecisionReplayEvaluation> EvaluateAsync(
        HttpContext requestContext,
        AuditLedgerRecord record,
        Reconstruction reconstruction,
        ReplayPolicy policy,
        CancellationToken cancellationToken)
    {
        bool tokensEnabled = _tokenOptions.Value.Enabled;
        string[] unknown = [.. reconstruction.Facts
            .Where(static fact => fact.Value.Value is null)
            .Select(static fact => fact.Key)];

        // Each unrecorded fact is evaluated under both values: 2^n runs for n unknowns, at most four here.
        List<(Dictionary<string, bool> Assumptions, DecisionReplayBranch Branch)> runs = [];
        for (int mask = 0; mask < 1 << unknown.Length; mask++)
        {
            Dictionary<string, bool> assumptions = new(StringComparer.Ordinal);
            for (int index = 0; index < unknown.Length; index++)
            {
                assumptions[unknown[index]] = (mask & (1 << index)) != 0;
            }

            bool administrator = ResolveFact(reconstruction, assumptions, DecisionReplayFactKeys.Administrator);
            bool capabilityGrant = ResolveFact(reconstruction, assumptions, DecisionReplayFactKeys.CapabilityGrant);

            GovernancePipelineRun run = await _harness.RunAsync(
                new GovernancePipelineRequest(
                    RequestContext: requestContext,
                    Endpoint: reconstruction.Endpoint!,
                    RequestPath: $"/api/v1/governed-operations/{reconstruction.Details.OperationKey}",
                    Actor: CreateRecordedActor(record, administrator, capabilityGrant),
                    Fields: new Dictionary<string, StringValues>(StringComparer.Ordinal),
                    Constraints: policy.Constraints,
                    DisabledConstraints: policy.Disabled,
                    DecisionPolicy: policy.DecisionPolicy,
                    CreateCapabilityValidator: recorder => GovernancePipelineHarness.Record(
                        CreateCapabilityValidator(tokensEnabled, capabilityGrant, record, reconstruction),
                        recorder),
                    TraceIdentifierPrefix: "decision-replay",
                    PolicyVersion: policy.PolicyVersion,
                    PolicyHash: policy.PolicyHash),
                cancellationToken).ConfigureAwait(false);

            runs.Add((assumptions, CreateBranch(record, run, policy.Disabled, assumptions, tokensEnabled)));
        }

        List<string> decisive = [.. unknown.Where(key => IsDecisive(runs, key))];
        bool partial = decisive.Count > 0;
        List<DecisionReplayBranch> allBranches = [.. runs.Select(static run => run.Branch)];

        // When no unrecorded fact changed the decision, one branch stands for all of them.
        List<DecisionReplayBranch> branches = partial
            ? allBranches
            : [allBranches[0] with { Assumptions = new Dictionary<string, bool>(StringComparer.Ordinal) }];

        return new DecisionReplayEvaluation(
            Label: policy.Label,
            Hypothetical: policy.HypotheticalReason is not null || policy.Disabled.Count > 0,
            HypotheticalReason: policy.HypotheticalReason
                ?? (policy.Disabled.Count > 0 ? "A what-if: constraints were left out of the policy." : null),
            PolicyVersion: policy.PolicyVersion ?? ReferenceApplicationGovernancePolicySet.Version,
            PolicyHash: policy.PolicyHash ?? ReferenceApplicationGovernancePolicySet.PolicyHash,
            DisabledConstraints: [.. ConstraintNames.Where(policy.Disabled.Contains)],
            PolicyChanges: policy.Changes,
            Partial: partial,
            DecisiveUnknownInputs: decisive,
            IgnoredUnknownInputs: [.. unknown.Except(decisive, StringComparer.Ordinal)],
            Branches: branches);
    }

    private IEndpointCapabilityGrantValidator CreateCapabilityValidator(
        bool tokensEnabled,
        bool capabilityGrant,
        AuditLedgerRecord record,
        Reconstruction reconstruction)
    {
        return tokensEnabled
            ? new RecordedGrantValidator(capabilityGrant, record.CapabilityTokenId, reconstruction.RecordedCapabilityDenial)
            : _claimValidator;
    }

    private DecisionReplayBranch CreateBranch(
        AuditLedgerRecord record,
        GovernancePipelineRun run,
        IReadOnlySet<string> disabled,
        Dictionary<string, bool> assumptions,
        bool tokensEnabled)
    {
        GovernanceDecision? final = run.Recorder.CapabilityOutput ?? run.Recorder.PolicyDecision;
        // The receipt carries AsiBackbone's canonical outcome name, which is the enum member name.
        string outcome = run.Receipt?.Outcome ?? final?.Outcome.ToString() ?? string.Empty;
        IReadOnlyList<string> reasonCodes = run.Receipt?.ReasonCodes ?? final?.ReasonCodes ?? [];

        return new DecisionReplayBranch(
            Assumptions: assumptions,
            Outcome: outcome,
            ReasonCodes: reasonCodes,
            Difference: Compare(record, outcome, reasonCodes),
            Constraints: GovernancePipelineHarness.CreateConstraintStages(run.Recorder, disabled),
            DecisionPolicy: GovernancePipelineHarness.CreateDecisionPolicyStage(run.Recorder),
            Capability: GovernancePipelineHarness.CreateCapabilityStage(
                run.Descriptor,
                run.Recorder,
                tokensEnabled ? RecordedGrantModel : PolicySimulator.ClaimsModel),
            Receipt: GovernancePipelineHarness.CreateReceipt(run.Receipt),
            EndpointResult: _harness.CreateEndpointResult(run.Result));
    }

    private static DecisionReplayDifference Compare(
        AuditLedgerRecord record,
        string outcome,
        IReadOnlyList<string> reasonCodes)
    {
        bool outcomeChanged = !string.Equals(record.Outcome, outcome, StringComparison.Ordinal);
        string[] added = [.. reasonCodes.Except(record.ReasonCodes, StringComparer.Ordinal)];
        string[] removed = [.. record.ReasonCodes.Except(reasonCodes, StringComparer.Ordinal)];

        return new DecisionReplayDifference(
            Matches: !outcomeChanged && record.ReasonCodes.SequenceEqual(reasonCodes, StringComparer.Ordinal),
            OutcomeChanged: outcomeChanged,
            AddedReasonCodes: added,
            RemovedReasonCodes: removed);
    }

    private static bool IsDecisive(
        List<(Dictionary<string, bool> Assumptions, DecisionReplayBranch Branch)> runs,
        string key)
    {
        // A fact is decisive when flipping it alone, with every other assumption held, changes the decision.
        foreach ((Dictionary<string, bool> assumptions, DecisionReplayBranch branch) in runs)
        {
            if (assumptions[key])
            {
                continue;
            }

            DecisionReplayBranch flipped = runs.First(other =>
                other.Assumptions[key] &&
                other.Assumptions.All(pair =>
                    string.Equals(pair.Key, key, StringComparison.Ordinal) || assumptions[pair.Key] == pair.Value)).Branch;
            if (!SameDecision(branch, flipped))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameDecision(DecisionReplayBranch left, DecisionReplayBranch right)
    {
        return string.Equals(left.Outcome, right.Outcome, StringComparison.Ordinal)
            && left.ReasonCodes.SequenceEqual(right.ReasonCodes, StringComparer.Ordinal)
            && string.Equals(left.EndpointResult.Kind, right.EndpointResult.Kind, StringComparison.Ordinal)
            && left.EndpointResult.StatusCode == right.EndpointResult.StatusCode;
    }

    private static bool ResolveFact(Reconstruction reconstruction, Dictionary<string, bool> assumptions, string key)
    {
        return reconstruction.Facts[key].Value ?? assumptions[key];
    }

    private async Task<AuditLedgerRecord?> FindRecordAsync(string decisionReceiptId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionReceiptId);

        string receiptId = decisionReceiptId.Trim();
        await using ApplicationDbContext dbContext = await _dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        string? recordId = await dbContext.AsiBackboneAuditLedgerRecords
            .AsNoTracking()
            .Where(record => record.DecisionReceiptId == receiptId ||
                (record.DecisionReceiptId == null && record.EventId == receiptId))
            .OrderBy(static record => record.RecordId)
            .Select(static record => record.RecordId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return recordId is null
            ? null
            : await new EfCoreAuditLedgerStore(dbContext, _loggerFactory.CreateLogger<EfCoreAuditLedgerStore>())
                .FindByRecordIdAsync(recordId, cancellationToken)
                .ConfigureAwait(false);
    }

    private Reconstruction Reconstruct(AuditLedgerRecord record)
    {
        (string? operationKey, Endpoint? endpoint, EndpointGovernanceDescriptor? descriptor) = FindOperation(record.OperationName);
        bool policyMatches = string.Equals(
            record.PolicyHash,
            ReferenceApplicationGovernancePolicySet.PolicyHash,
            StringComparison.Ordinal);

        HashSet<string> reasons = new(record.ReasonCodes, StringComparer.Ordinal);
        bool denied = string.Equals(record.Outcome, _deniedOutcome, StringComparison.Ordinal);
        bool administratorPolicy = GovernanceMetadata.ContainsPolicy<AdministratorActionPolicy>(record.Metadata);
        bool scopesDeclared = record.Metadata.TryGetValue(_capabilityScopesKey, out string? scopes) &&
            !string.IsNullOrWhiteSpace(scopes);
        string? capabilityDenial = record.ReasonCodes.FirstOrDefault(IsCapabilityDenialCode);

        Fact administrator = InferAdministrator(reasons, denied, administratorPolicy, policyMatches);
        Fact capabilityGrant = InferCapabilityGrant(record, denied, scopesDeclared, capabilityDenial);

        List<string> notices = [];
        if (!policyMatches)
        {
            notices.Add(record.PolicyHash is null
                ? "The decision records no policy hash, so it cannot be tied to the current policy. Replaying it is a what-if, not a re-verification."
                : $"The decision was recorded under policy {record.PolicyVersion ?? "(unversioned)"} with a different hash from the current policy {ReferenceApplicationGovernancePolicySet.Version}. Replaying it is a what-if, not a re-verification.");
        }

        bool declarationsChanged = descriptor is not null && !SameDeclarations(record.Metadata, descriptor);
        if (declarationsChanged)
        {
            notices.Add("The endpoint's governance declarations have changed since this decision. The replay uses the current declarations.");
        }

        string? notReplayable = endpoint is null
            ? "The operation this decision was made for is not one of the sample governed endpoints mapped in this application."
            : null;

        DecisionReplayDetails details = new(
            DecisionReceiptId: record.DecisionReceiptId,
            RecordId: record.RecordId,
            OccurredUtc: record.OccurredUtc,
            OperationName: record.OperationName,
            OperationKey: operationKey,
            ActorId: record.ActorId,
            ActorType: record.ActorType.ToString(),
            ActorDisplayName: record.ActorDisplayName,
            Outcome: record.Outcome,
            ReasonCodes: record.ReasonCodes,
            CorrelationId: record.CorrelationId,
            CapabilityGrantId: record.CapabilityTokenId,
            Policy: new DecisionReplayPolicyComparison(
                RecordedVersion: record.PolicyVersion,
                RecordedHash: record.PolicyHash,
                CurrentVersion: ReferenceApplicationGovernancePolicySet.Version,
                CurrentHash: ReferenceApplicationGovernancePolicySet.PolicyHash,
                Matches: policyMatches,
                EndpointDeclarationsChanged: declarationsChanged,
                Notices: notices),
            Evidence: Verify(record),
            Inputs: CreateInputs(record, operationKey, administrator, capabilityGrant, capabilityDenial),
            Replayable: notReplayable is null,
            NotReplayableReason: notReplayable);

        return new Reconstruction(
            details,
            endpoint,
            new Dictionary<string, Fact>(StringComparer.Ordinal)
            {
                [DecisionReplayFactKeys.Administrator] = administrator,
                [DecisionReplayFactKeys.CapabilityGrant] = capabilityGrant
            },
            capabilityDenial is not null && capabilityDenial.StartsWith(_capabilityGrantReasonPrefix, StringComparison.Ordinal)
                ? capabilityDenial
                : null);
    }

    private static Fact InferAdministrator(
        HashSet<string> reasons,
        bool denied,
        bool administratorPolicy,
        bool policyMatches)
    {
        if (reasons.Contains(AdministratorRoleConstraint.DeniedReasonCode))
        {
            return new Fact(false, $"The recorded reasons include {AdministratorRoleConstraint.DeniedReasonCode}.");
        }

        // The administrator constraint runs first, so under the current policy a decision that was not denied, or was
        // denied only by the later capability check, passed it.
        bool passed = !denied || (reasons.Count > 0 && reasons.All(IsCapabilityDenialCode));
        return administratorPolicy && policyMatches && passed
            ? new Fact(true, "The policy checks the administrator role for this operation, and the decision was not denied for lacking it.")
            : administratorPolicy && !policyMatches
            ? new Fact(null, "The decision was recorded under a different policy, so its outcome does not show whether the role was checked.")
            : administratorPolicy
            ? new Fact(null, "The decision was denied before the administrator role was shown to pass.")
            : new Fact(null, "The recorded policy does not check the administrator role for this operation, so the ledger holds no evidence of it.");
    }

    private static Fact InferCapabilityGrant(
        AuditLedgerRecord record,
        bool denied,
        bool scopesDeclared,
        string? capabilityDenial)
    {
        return !string.IsNullOrWhiteSpace(record.CapabilityTokenId)
            ? new Fact(true, $"The receipt names accepted capability grant {record.CapabilityTokenId}.")
            : capabilityDenial is not null
            ? new Fact(false, $"The recorded reasons include {capabilityDenial}.")
            : scopesDeclared && !denied
            ? new Fact(true, "The capability check runs for every decision that is not already denied, and this one was not denied.")
            : scopesDeclared
            ? new Fact(null, "The decision was denied before the capability check ran, so the ledger holds no evidence of the grant.")
            : new Fact(null, "The endpoint declares no capability scope, so the grant was never checked.");
    }

    private static List<DecisionReplayInput> CreateInputs(
        AuditLedgerRecord record,
        string? operationKey,
        Fact administrator,
        Fact capabilityGrant,
        string? capabilityDenial)
    {
        List<string> declarations = [];
        if (record.Metadata.TryGetValue(_policyTypesKey, out string? policyTypes))
        {
            declarations.Add($"policies {policyTypes}");
        }

        if (record.Metadata.TryGetValue(_capabilityScopesKey, out string? scopes))
        {
            declarations.Add($"scopes {scopes}");
        }

        string? model = !string.IsNullOrWhiteSpace(record.CapabilityTokenId) ||
            (capabilityDenial?.StartsWith(_capabilityGrantReasonPrefix, StringComparison.Ordinal) ?? false)
            ? "Signed capability token"
            : string.Equals(capabilityDenial, ClaimEndpointCapabilityGrantValidator.DeniedReasonCode, StringComparison.Ordinal)
            ? "Permission claims"
            : null;
        DecisionReplayInput modelInput = model is null
            ? new DecisionReplayInput(
                "capabilityModel",
                "Capability grant model",
                DecisionReplayInputStatuses.Unrecoverable,
                Value: null,
                "Neither a grant identifier nor a grant denial is recorded, so the model in force is not known.")
            : new DecisionReplayInput(
                "capabilityModel",
                "Capability grant model",
                DecisionReplayInputStatuses.Inferred,
                model,
                "Inferred from the recorded grant identifier or denial reason.");

        return
        [
            new DecisionReplayInput(
                "operation",
                "Operation",
                DecisionReplayInputStatuses.Recorded,
                record.OperationName,
                operationKey is null
                    ? "Recorded on the receipt. It is not one of the sample endpoints mapped in this application."
                    : $"Recorded on the receipt, and mapped to the '{operationKey}' sample endpoint."),
            new DecisionReplayInput(
                "declarations",
                "Endpoint governance declarations",
                DecisionReplayInputStatuses.Recorded,
                declarations.Count == 0 ? null : string.Join("; ", declarations),
                "Recorded in the receipt metadata: the policy markers and capability scopes the endpoint declared."),
            new DecisionReplayInput(
                "actor",
                "Actor identifier",
                DecisionReplayInputStatuses.Recorded,
                record.ActorId,
                "Recorded on the receipt. Only the identifier and type are stored, not the actor's claims."),
            CreateFactInput(DecisionReplayFactKeys.Administrator, "Administrator role", administrator),
            CreateFactInput(DecisionReplayFactKeys.CapabilityGrant, "Capability grant", capabilityGrant),
            modelInput,
            new DecisionReplayInput(
                "payload",
                "Request payload",
                DecisionReplayInputStatuses.Unrecoverable,
                Value: null,
                "The ledger stores no payload. The policy does not read it; a signed capability token binds it, and the token is not stored either.")
        ];
    }

    private static DecisionReplayInput CreateFactInput(string key, string name, Fact fact)
    {
        return fact.Value is { } value
            ? new DecisionReplayInput(key, name, DecisionReplayInputStatuses.Inferred, value ? "yes" : "no", fact.Explanation)
            : new DecisionReplayInput(key, name, DecisionReplayInputStatuses.Unrecoverable, null, fact.Explanation);
    }

    private static DecisionReplayEvidence Verify(AuditLedgerRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.RecordHash))
        {
            return new DecisionReplayEvidence(GovernanceEvidenceVerificationStatuses.NotHashed, null, null, null);
        }

        CanonicalPayloadHash computed = AuditLedgerRecordHashing.ComputeHash(record);
        return new DecisionReplayEvidence(
            string.Equals(record.RecordHash, computed.HashValue, StringComparison.Ordinal)
                ? GovernanceEvidenceVerificationStatuses.Verified
                : GovernanceEvidenceVerificationStatuses.Mismatch,
            record.RecordHash,
            computed.HashValue,
            computed.HashAlgorithm);
    }

    private (string? Key, Endpoint? Endpoint, EndpointGovernanceDescriptor? Descriptor) FindOperation(string operationName)
    {
        foreach ((string key, string actionName) in PolicySimulator.GovernedOperations)
        {
            if (_harness.FindGovernedEndpoint(actionName) is not { } endpoint)
            {
                continue;
            }

            var descriptor = EndpointGovernanceDescriptor.FromEndpoint(endpoint);
            if (string.Equals(descriptor.OperationName, operationName, StringComparison.Ordinal))
            {
                return (key, endpoint, descriptor);
            }
        }

        return (null, null, null);
    }

    private string FindAuditedRecordOperationName()
    {
        Endpoint endpoint = _harness.FindGovernedEndpoint(nameof(GovernedOperationsController.CreateRecord))
            ?? throw new InvalidOperationException("The create-audited-record endpoint is not mapped.");
        return EndpointGovernanceDescriptor.FromEndpoint(endpoint).OperationName;
    }

    private static bool SameDeclarations(IReadOnlyDictionary<string, string> recorded, EndpointGovernanceDescriptor descriptor)
    {
        IReadOnlyDictionary<string, string> current = descriptor.ToMetadata(EndpointGovernanceMetadataMode.Full);
        return string.Equals(
                recorded.GetValueOrDefault(_policyTypesKey),
                current.GetValueOrDefault(_policyTypesKey),
                StringComparison.Ordinal)
            && string.Equals(
                recorded.GetValueOrDefault(_capabilityScopesKey),
                current.GetValueOrDefault(_capabilityScopesKey),
                StringComparison.Ordinal);
    }

    private ClaimsPrincipal CreateRecordedActor(AuditLedgerRecord record, bool administrator, bool capabilityGrant)
    {
        if (string.Equals(record.ActorId, GovernanceActorContext.UnknownActorId, StringComparison.Ordinal))
        {
            return new ClaimsPrincipal(new ClaimsIdentity());
        }

        ApplicationAuthorizationOptions options = _authorizationOptions.Value;
        List<Claim> claims = [new(ApplicationClaimTypes.Subject, record.ActorId)];
        if (!string.IsNullOrWhiteSpace(record.ActorDisplayName))
        {
            claims.Add(new Claim(ApplicationClaimTypes.Name, record.ActorDisplayName));
        }

        if (record.ActorType != GovernanceActorType.Human)
        {
            claims.Add(new Claim(_actorOptions.Value.ActorTypeClaimType, record.ActorType.ToString()));
        }

        if (administrator && options.AdministratorRoles.Length > 0)
        {
            claims.Add(new Claim(options.RoleClaimType, options.AdministratorRoles[0]));
        }

        if (capabilityGrant)
        {
            claims.Add(new Claim(
                options.PermissionClaimType,
                ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            "DecisionReplay",
            ApplicationClaimTypes.Name,
            options.RoleClaimType));
    }

    private static bool IsCapabilityDenialCode(string reasonCode)
    {
        return string.Equals(reasonCode, ClaimEndpointCapabilityGrantValidator.DeniedReasonCode, StringComparison.Ordinal)
            || reasonCode.StartsWith(_capabilityGrantReasonPrefix, StringComparison.Ordinal);
    }

    private static string[] ReadReasonCodes(string reasonCodesJson)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(reasonCodesJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record LedgerKey(string RecordId, DateTimeOffset RecordedUtc);

    private sealed record Fact(bool? Value, string Explanation);

    private sealed record Reconstruction(
        DecisionReplayDetails Details,
        Endpoint? Endpoint,
        Dictionary<string, Fact> Facts,
        string? RecordedCapabilityDenial);

    private sealed record ReplayPolicy(
        string Label,
        IReadOnlyList<IGovernanceConstraint<GovernanceEvaluationContext>> Constraints,
        IReadOnlySet<string> Disabled,
        IGovernanceDecisionPolicy<GovernanceEvaluationContext> DecisionPolicy,
        string? PolicyVersion,
        string? PolicyHash,
        IReadOnlyList<string> Changes,
        string? HypotheticalReason);

    private sealed class RecordedGrantValidator(bool hasGrant, string? grantId, string? recordedDenial)
        : IEndpointCapabilityGrantValidator
    {
        public ValueTask<GovernanceDecision> ValidateAsync(
            HttpContext httpContext,
            EndpointGovernanceDescriptor descriptor,
            GovernanceDecision currentDecision,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            ArgumentNullException.ThrowIfNull(currentDecision);
            cancellationToken.ThrowIfCancellationRequested();

            if (currentDecision.IsDenied)
            {
                return ValueTask.FromResult(currentDecision);
            }

            // The token itself is not stored, so it cannot be verified again, and a replay must not consume a use. The
            // grant outcome is taken from the record instead.
            if (hasGrant)
            {
                if (!string.IsNullOrWhiteSpace(grantId))
                {
                    httpContext.Features.Set<IEndpointCapabilityGrantFeature>(new EndpointCapabilityGrantFeature(grantId));
                }

                return ValueTask.FromResult(currentDecision);
            }

            return ValueTask.FromResult(GovernanceDecision.Deny(
                recordedDenial ?? SignedCapabilityGrantReasonCodes.Missing,
                "Replayed from the record: no valid capability token was presented.",
                correlationId: currentDecision.CorrelationId,
                traceId: currentDecision.TraceId,
                policyVersion: currentDecision.PolicyVersion,
                policyHash: currentDecision.PolicyHash));
        }
    }
}
