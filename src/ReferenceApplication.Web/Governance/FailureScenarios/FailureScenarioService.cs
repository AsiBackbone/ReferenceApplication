using System.Security.Claims;
using AsiBackbone.AspNetCore.Actors;
using AsiBackbone.AspNetCore.CapabilityGrants;
using AsiBackbone.AspNetCore.Endpoints;
using AsiBackbone.Core.Actors;
using AsiBackbone.Core.Audit;
using AsiBackbone.Core.Decisions;
using AsiBackbone.EntityFrameworkCore.Audit;
using AsiBackbone.EntityFrameworkCore.Persistence;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ReferenceApplication.Infrastructure.Data;
using ReferenceApplication.Infrastructure.Data.Auditing;
using ReferenceApplication.Infrastructure.Data.Entities;
using ReferenceApplication.Web.Authentication.Options;
using ReferenceApplication.Web.Controllers.Api;

namespace ReferenceApplication.Web.Governance.FailureScenarios;

/// <summary>
/// Lists, arms, and reports on the Development-only failure scenarios.
/// </summary>
public interface IFailureScenarioService
{
    /// <summary>
    /// Lists the scenarios, with whether an actor can run each one now.
    /// </summary>
    /// <param name="actor">The actor viewing the scenarios.</param>
    /// <returns>The scenarios, in order.</returns>
    IReadOnlyList<FailureScenarioView> GetScenarios(ClaimsPrincipal actor);

    /// <summary>
    /// Arms a scenario for the current actor's next request, or, for the evidence-tampering scenario, runs it.
    /// </summary>
    /// <param name="scenarioId">The scenario to run.</param>
    /// <param name="correlationId">For the evidence-tampering scenario, the decision to copy; the latest when omitted.</param>
    /// <param name="httpContext">The request asking to run the scenario.</param>
    /// <param name="cancellationToken">A token that cancels the call.</param>
    /// <returns>The armed run, the tampering result, or why the scenario cannot run.</returns>
    Task<FailureScenarioStartResult> StartAsync(
        string scenarioId,
        string? correlationId,
        HttpContext httpContext,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports what a run's request did and what evidence it left behind.
    /// </summary>
    /// <param name="runId">The run identifier.</param>
    /// <param name="cancellationToken">A token that cancels the call.</param>
    /// <returns>The report, or <see langword="null" /> when the run is unknown.</returns>
    Task<FailureScenarioRunReport?> GetRunAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears a run's delivery fault, so the outbox's next attempt can deliver the entry.
    /// </summary>
    /// <param name="runId">The run identifier.</param>
    /// <returns><see langword="true" /> when the run held an active delivery fault.</returns>
    bool ClearDeliveryFault(string runId);
}

/// <summary>
/// Lists, arms, and reports on the Development-only failure scenarios.
/// </summary>
/// <remarks>
/// <para>
/// Arming adds nothing to the governed request path: the scenario's request is the browser's own request to the real
/// governed endpoint, and the seams that inject faults act only on the request that consumed the run. Reports are
/// read-only queries of the evidence that request left in the demonstration database.
/// </para>
/// <para>
/// The two token scenarios issue a real signed capability token for the current actor, after the same entitlement
/// check the issuance endpoint applies. The expired-token scenario issues it from a clock set back past the token's
/// lifetime and the validator's clock skew, so the token is already expired when it is presented.
/// </para>
/// </remarks>
internal sealed class FailureScenarioService(
    FailureScenarioRegistry registry,
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    IGovernanceEvidenceQuery evidenceQuery,
    IGovernanceEvidenceVerifier evidenceVerifier,
    IHttpGovernanceActorContextResolver actorContextResolver,
    IOptions<ApplicationAuthorizationOptions> authorizationOptions,
    IOptions<CapabilityTokenOptions> tokenOptions,
    IOptions<ApplicationAuditCompletionOutboxOptions> outboxOptions,
    EndpointDataSource endpointDataSource,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory,
    IServiceProvider serviceProvider) : IFailureScenarioService
{
    private const string _tokenPayloadField = "Note";
    private const string _approvedPayload = "approved";
    private const string _tamperedPayload = "tampered";
    private static readonly TimeSpan _expiredTokenMargin = TimeSpan.FromMinutes(1);

    private readonly FailureScenarioRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory =
        dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    private readonly IGovernanceEvidenceQuery _evidenceQuery =
        evidenceQuery ?? throw new ArgumentNullException(nameof(evidenceQuery));
    private readonly IGovernanceEvidenceVerifier _evidenceVerifier =
        evidenceVerifier ?? throw new ArgumentNullException(nameof(evidenceVerifier));
    private readonly IHttpGovernanceActorContextResolver _actorContextResolver =
        actorContextResolver ?? throw new ArgumentNullException(nameof(actorContextResolver));
    private readonly IOptions<ApplicationAuthorizationOptions> _authorizationOptions =
        authorizationOptions ?? throw new ArgumentNullException(nameof(authorizationOptions));
    private readonly IOptions<CapabilityTokenOptions> _tokenOptions =
        tokenOptions ?? throw new ArgumentNullException(nameof(tokenOptions));
    private readonly IOptions<ApplicationAuditCompletionOutboxOptions> _outboxOptions =
        outboxOptions ?? throw new ArgumentNullException(nameof(outboxOptions));
    private readonly EndpointDataSource _endpointDataSource =
        endpointDataSource ?? throw new ArgumentNullException(nameof(endpointDataSource));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    private readonly IServiceProvider _serviceProvider =
        serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    /// <inheritdoc />
    public IReadOnlyList<FailureScenarioView> GetScenarios(ClaimsPrincipal actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return [.. FailureScenarioCatalog.Scenarios.Select(scenario => CreateView(scenario, actor))];
    }

    /// <inheritdoc />
    public async Task<FailureScenarioStartResult> StartAsync(
        string scenarioId,
        string? correlationId,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (FailureScenarioCatalog.Find(scenarioId) is not { } scenario)
        {
            return FailureScenarioStartResult.Refused(StatusCodes.Status404NotFound, "The failure scenario is unknown.");
        }

        FailureScenarioView view = CreateView(scenario, httpContext.User);
        if (!view.Available)
        {
            return FailureScenarioStartResult.Refused(StatusCodes.Status409Conflict, view.UnavailableReason!);
        }

        if (scenario.Path is null)
        {
            FailureScenarioTamperingReport? report = await TamperAsync(correlationId, cancellationToken).ConfigureAwait(false);
            return report is null
                ? FailureScenarioStartResult.Refused(
                    StatusCodes.Status409Conflict,
                    correlationId is null
                        ? "The ledger holds no decision yet. Run a governed action first."
                        : $"The ledger holds no decision for correlation {correlationId}.")
                : FailureScenarioStartResult.Tampered(report);
        }

        IGovernanceActorContext actor = _actorContextResolver.ResolveActorContext();
        if (!actor.IsAuthenticated)
        {
            return FailureScenarioStartResult.Refused(StatusCodes.Status403Forbidden, "The scenario needs an authenticated actor.");
        }

        FailureScenarioRequest? request = scenario.Id switch
        {
            FailureScenarioCatalog.PayloadTampering or FailureScenarioCatalog.ExpiredToken =>
                await CreateTokenRequestAsync(scenario, actor, httpContext, cancellationToken).ConfigureAwait(false),
            FailureScenarioCatalog.MissingCapabilityGrant => CreateRequest(
                scenario.Path,
                new Dictionary<string, string>(StringComparer.Ordinal)),
            _ => CreateRequest(scenario.Path, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Title"] = $"Failure scenario {scenario.Number}: {scenario.Title}"
            })
        };

        return request is null
            ? FailureScenarioStartResult.Refused(
                StatusCodes.Status403Forbidden,
                "A capability token was not issued: the actor does not hold the capability the endpoint requires.")
            : FailureScenarioStartResult.Armed(_registry.Arm(scenario.Id, actor.ActorId, request));
    }

    /// <inheritdoc />
    public async Task<FailureScenarioRunReport?> GetRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId) || _registry.Find(runId.Trim()) is not { } run)
        {
            return null;
        }

        FailureScenarioDefinition scenario = FailureScenarioCatalog.Find(run.ScenarioId)
            ?? throw new InvalidOperationException($"The run names an unknown scenario '{run.ScenarioId}'.");
        FailureScenarioView view = CreateView(scenario, actor: null);

        if (run.CorrelationId is not { } correlationId)
        {
            return new FailureScenarioRunReport(view, run, ActionBody: null, [], 0, Outbox: null, Reconciliation: null, Timeline: null);
        }

        await using ApplicationDbContext dbContext = await _dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        List<FailureScenarioDecision> decisions = await ReadDecisionsAsync(dbContext, correlationId, cancellationToken)
            .ConfigureAwait(false);
        List<string> mutationBatchIds = await dbContext.AuditRecords
            .AsNoTracking()
            .Where(record => record.CorrelationId == correlationId)
            .Select(static record => record.MutationBatchId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        FailureScenarioOutboxEntry? outbox = await ReadOutboxAsync(dbContext, correlationId, cancellationToken)
            .ConfigureAwait(false);
        FailureScenarioReconciliation? reconciliation = string.Equals(
                scenario.Id,
                FailureScenarioCatalog.ExecutionFailsAfterApproval,
                StringComparison.Ordinal)
            ? await ReadReconciliationAsync([.. mutationBatchIds.Distinct(StringComparer.Ordinal)], cancellationToken)
                .ConfigureAwait(false)
            : null;
        GovernanceEvidenceTimelineResponse? timeline = await _evidenceQuery
            .FindAsync(correlationId, cancellationToken)
            .ConfigureAwait(false);

        return new FailureScenarioRunReport(
            view,
            run,
            DescribeActionBody(run),
            decisions,
            mutationBatchIds.Count,
            outbox,
            reconciliation,
            timeline);
    }

    /// <inheritdoc />
    public bool ClearDeliveryFault(string runId)
    {
        return !string.IsNullOrWhiteSpace(runId) && _registry.ClearDeliveryFault(runId.Trim());
    }

    private FailureScenarioView CreateView(FailureScenarioDefinition scenario, ClaimsPrincipal? actor)
    {
        string? unavailableReason = null;
        if (scenario.RequiresCapabilityTokens && !_tokenOptions.Value.Enabled)
        {
            unavailableReason = "Signed capability tokens are disabled. Set ReferenceApplication:CapabilityTokens:Enabled " +
                "to true, with a signing key, to run this scenario.";
        }
        else if (scenario.RequiresCapabilityTokens && actor is not null && !HoldsCapability(actor))
        {
            unavailableReason = "The scenario issues a real capability token, which requires the consequential " +
                "capability. Apply it with the Development demo identity.";
        }

        return new FailureScenarioView(
            scenario.Id,
            scenario.Number,
            scenario.Title,
            scenario.Path,
            scenario.Teaches,
            scenario.Expectation,
            unavailableReason is null,
            unavailableReason);
    }

    private bool HoldsCapability(ClaimsPrincipal actor)
    {
        return actor.HasClaim(
            _authorizationOptions.Value.PermissionClaimType,
            ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope);
    }

    private static FailureScenarioRequest CreateRequest(
        string path,
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        return new FailureScenarioRequest(
            HttpMethods.Post,
            path,
            fields,
            headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    private async Task<FailureScenarioRequest?> CreateTokenRequestAsync(
        FailureScenarioDefinition scenario,
        IGovernanceActorContext actor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        Endpoint endpoint = FindCapabilityEndpoint()
            ?? throw new InvalidOperationException("The capability-protected governed endpoint is not mapped.");
        var descriptor = EndpointGovernanceDescriptor.FromEndpoint(endpoint);

        // The issuance endpoint's entitlement check: a token is issued only to an actor who holds the capability.
        GovernanceDecision entitlement = await _serviceProvider
            .GetRequiredService<ClaimEndpointCapabilityGrantValidator>()
            .ValidateAsync(httpContext, descriptor, GovernanceDecision.Allow(), cancellationToken)
            .ConfigureAwait(false);
        if (entitlement.IsDenied)
        {
            return null;
        }

        CapabilityTokenIssuer issuer = _serviceProvider.GetRequiredService<CapabilityTokenIssuer>();
        if (string.Equals(scenario.Id, FailureScenarioCatalog.ExpiredToken, StringComparison.Ordinal))
        {
            CapabilityTokenOptions options = _tokenOptions.Value;
            TimeSpan setBack = TimeSpan.FromSeconds(options.LifetimeSeconds + options.AllowedClockSkewSeconds) +
                _expiredTokenMargin;
            issuer = new CapabilityTokenIssuer(
                _tokenOptions,
                _serviceProvider.GetRequiredService<CapabilityTokenSigningKey>(),
                new OffsetTimeProvider(_timeProvider, -setBack));
        }

        IssuedCapabilityToken token = await issuer
            .IssueAsync(
                new CapabilityTokenIssueRequest(
                    actor.ActorId,
                    descriptor.OperationName,
                    descriptor.CapabilityScopes,
                    [new KeyValuePair<string, StringValues>(_tokenPayloadField, _approvedPayload)]),
                cancellationToken)
            .ConfigureAwait(false);

        string postedPayload = string.Equals(scenario.Id, FailureScenarioCatalog.PayloadTampering, StringComparison.Ordinal)
            ? _tamperedPayload
            : _approvedPayload;
        return CreateRequest(
            FailureScenarioCatalog.CapabilityPath,
            new Dictionary<string, string>(StringComparer.Ordinal) { [_tokenPayloadField] = postedPayload },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [SignedCapabilityGrantOptions.DefaultTokenHeaderName] = token.Token
            });
    }

    private Endpoint? FindCapabilityEndpoint()
    {
        return _endpointDataSource.Endpoints.FirstOrDefault(static endpoint =>
            endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action &&
            action.ControllerTypeInfo.AsType() == typeof(GovernedOperationsController) &&
            string.Equals(action.ActionName, nameof(GovernedOperationsController.ExecuteCapabilityAction), StringComparison.Ordinal));
    }

    private async Task<FailureScenarioTamperingReport?> TamperAsync(string? correlationId, CancellationToken cancellationToken)
    {
        string? recordId;
        await using (ApplicationDbContext dbContext = await _dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            IQueryable<AuditLedgerRecordEntity> records = dbContext.AsiBackboneAuditLedgerRecords.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                string normalized = correlationId.Trim();
                records = records.Where(record => record.CorrelationId == normalized);
            }

            // SQLite cannot order by DateTimeOffset, so the newest record is chosen in memory.
            recordId = (await records
                    .Select(static record => new { record.RecordId, record.RecordedUtc })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                .OrderByDescending(static record => record.RecordedUtc)
                .Select(static record => record.RecordId)
                .FirstOrDefault();
        }

        if (recordId is null)
        {
            return null;
        }

        AuditLedgerRecord stored;
        await using (ApplicationDbContext dbContext = await _dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            var store = new EfCoreAuditLedgerStore(dbContext, _loggerFactory.CreateLogger<EfCoreAuditLedgerStore>());
            stored = await store.FindByRecordIdAsync(recordId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"The ledger record '{recordId}' could not be read.");
        }

        // Flip the outcome, as an attacker rewriting a denial into permission would.
        string tamperedOutcome = string.Equals(stored.Outcome, nameof(GovernanceDecisionOutcome.Allowed), StringComparison.Ordinal)
            ? nameof(GovernanceDecisionOutcome.Denied)
            : nameof(GovernanceDecisionOutcome.Allowed);
        GovernanceEvidenceRecordVerification unchanged = GovernanceEvidenceVerifier.VerifyRecord(Copy(stored, stored.Outcome));
        GovernanceEvidenceRecordVerification tampered = GovernanceEvidenceVerifier.VerifyRecord(Copy(stored, tamperedOutcome));

        // The stored row is read and verified again through the explorer's verifier, to show it was never changed.
        GovernanceEvidenceVerificationResponse? storedVerification = stored.CorrelationId is null
            ? null
            : await _evidenceVerifier.VerifyAsync(stored.CorrelationId, cancellationToken).ConfigureAwait(false);
        GovernanceEvidenceRecordVerification storedRecord = storedVerification?.Records
            .FirstOrDefault(record => string.Equals(record.RecordId, stored.RecordId, StringComparison.Ordinal))
            ?? GovernanceEvidenceVerifier.VerifyRecord(stored);

        return new FailureScenarioTamperingReport(
            stored.CorrelationId ?? string.Empty,
            stored.RecordId,
            "outcome",
            stored.Outcome,
            tamperedOutcome,
            unchanged,
            tampered,
            storedRecord);
    }

    private static AuditLedgerRecord Copy(AuditLedgerRecord record, string outcome)
    {
        return AuditLedgerRecord.FromDecisionReceipt(
            new OutcomeOverrideReceipt(record, outcome),
            recordId: record.RecordId,
            recordedUtc: record.RecordedUtc,
            handshakeId: record.HandshakeId,
            acknowledgmentId: record.AcknowledgmentId,
            capabilityTokenId: record.CapabilityTokenId,
            previousRecordHash: record.PreviousRecordHash,
            recordHash: record.RecordHash,
            signatureKeyId: record.SignatureKeyId,
            signatureAlgorithm: record.SignatureAlgorithm,
            signatureValue: record.SignatureValue,
            signingHash: record.SigningHash,
            signatureKeyVersion: record.SignatureKeyVersion,
            signatureProvider: record.SignatureProvider,
            signedUtc: record.SignedUtc,
            schemaVersion: record.SchemaVersion);
    }

    private static string? DescribeActionBody(FailureScenarioRunState run)
    {
        return run.Status != FailureScenarioRunStatuses.Completed
            ? null
            : run.Faults.Any(static fault => fault.Point == FailureScenarioFaultPoints.MutationSave)
            ? "ran-rolled-back"
            : run.StatusCode is >= 200 and < 300
            ? "ran"
            : "not-run";
    }

    private async Task<List<FailureScenarioDecision>> ReadDecisionsAsync(
        ApplicationDbContext dbContext,
        string correlationId,
        CancellationToken cancellationToken)
    {
        string[] recordIds = await dbContext.AsiBackboneAuditLedgerRecords
            .AsNoTracking()
            .Where(record => record.CorrelationId == correlationId)
            .Select(static record => record.RecordId)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        var store = new EfCoreAuditLedgerStore(dbContext, _loggerFactory.CreateLogger<EfCoreAuditLedgerStore>());
        List<FailureScenarioDecision> decisions = [];
        foreach (string recordId in recordIds)
        {
            if (await store.FindByRecordIdAsync(recordId, cancellationToken).ConfigureAwait(false) is { } record)
            {
                decisions.Add(new FailureScenarioDecision(
                    record.RecordId,
                    record.OperationName,
                    record.Outcome,
                    record.ReasonCodes,
                    record.CapabilityTokenId,
                    record.RecordedUtc));
            }
        }

        return [.. decisions.OrderBy(static decision => decision.RecordedUtc)];
    }

    private async Task<FailureScenarioOutboxEntry?> ReadOutboxAsync(
        ApplicationDbContext dbContext,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ApplicationAuditCompletionOutboxEntry? entry = await dbContext.ApplicationAuditCompletionOutboxEntries
            .AsNoTracking()
            .Where(candidate => candidate.CorrelationId == correlationId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }

        string lifecycleEventId = AsiBackboneAuditCompletionPublisher.CreateLifecycleEventId(entry.IdempotencyKey);
        bool lifecycleAppended = await dbContext.AsiBackboneDecisionReceiptLifecycleEvents
            .AsNoTracking()
            .AnyAsync(lifecycleEvent => lifecycleEvent.EventId == lifecycleEventId, cancellationToken)
            .ConfigureAwait(false);

        return new FailureScenarioOutboxEntry(
            entry.Status,
            entry.RetryCount,
            _outboxOptions.Value.MaxRetryAttempts,
            ToUtc(entry.LastAttemptUtc),
            ToUtc(entry.NextAttemptUtc),
            ToUtc(entry.DeliveredUtc),
            entry.LastErrorCode,
            entry.LastErrorMessage,
            lifecycleAppended);
    }

    private async Task<FailureScenarioReconciliation> ReadReconciliationAsync(
        IReadOnlyList<string> mutationBatchIds,
        CancellationToken cancellationToken)
    {
        const string noBatchExplanation =
            "Reconciliation compares committed mutation-audit batches with their audit completions. The failed mutation " +
            "rolled back with its audit, so it left no batch and no outbox entry, and reconciliation has nothing about " +
            "this request to report. The gap shows in the ledger instead: an allowed decision with no lifecycle event.";

        if (_serviceProvider.GetService<IApplicationAuditReconciler>() is not { } reconciler)
        {
            return new FailureScenarioReconciliation(
                Registered: false,
                LastRunUtc: null,
                OpenFindingCount: 0,
                mutationBatchIds,
                [],
                "Reconciliation is not registered in this host; enable the pipeline inspector to register it. " +
                noBatchExplanation);
        }

        ApplicationAuditReconciliationSummary summary = await reconciler
            .GetSummaryAsync(cancellationToken)
            .ConfigureAwait(false);
        HashSet<string> batches = [.. mutationBatchIds];
        IReadOnlyList<ApplicationAuditReconciliationFindingItem> findings = batches.Count == 0
            ? []
            : await reconciler
                .QueryFindingsAsync(new ApplicationAuditReconciliationQuery(), cancellationToken)
                .ConfigureAwait(false);

        return new FailureScenarioReconciliation(
            Registered: true,
            ToUtc(summary.LastRunUtc),
            summary.OpenFindingCount,
            mutationBatchIds,
            [.. findings.Where(finding => batches.Contains(finding.MutationBatchId)).Select(static finding => finding.ReasonCode)],
            noBatchExplanation);
    }

    private static DateTimeOffset? ToUtc(DateTime? value)
    {
        return value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
    }

    private sealed class OffsetTimeProvider(TimeProvider inner, TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return inner.GetUtcNow() + offset;
        }
    }

    /// <summary>
    /// Presents a ledger record as a decision receipt with a different outcome, so a copy can be built with one field
    /// changed and every other value as stored.
    /// </summary>
    private sealed class OutcomeOverrideReceipt(AuditLedgerRecord inner, string outcome) : IDecisionReceipt
    {
        public string EventId => inner.EventId;

        public string? DecisionReceiptId => inner.DecisionReceiptId;

        public string SchemaVersion => inner.SchemaVersion;

        public DateTimeOffset OccurredUtc => inner.OccurredUtc;

        public string ActorId => inner.ActorId;

        public GovernanceActorType ActorType => inner.ActorType;

        public string? ActorDisplayName => inner.ActorDisplayName;

        public string OperationName => inner.OperationName;

        public string Outcome => outcome;

        public IReadOnlyList<string> ReasonCodes => inner.ReasonCodes;

        public string? CorrelationId => inner.CorrelationId;

        public string? TraceId => inner.TraceId;

        public string? SpanId => inner.SpanId;

        public string? ParentSpanId => inner.ParentSpanId;

        public long? DecisionLatencyMs => inner.DecisionLatencyMs;

        public string? ConstraintSetHash => inner.ConstraintSetHash;

        public int? ConstraintCount => inner.ConstraintCount;

        public double? RiskScore => inner.RiskScore;

        public string? PolicyScope => inner.PolicyScope;

        public string? TenantHash => inner.TenantHash;

        public string? OrganizationHash => inner.OrganizationHash;

        public string? EmitterStatus => inner.EmitterStatus;

        public string? EmitterProvider => inner.EmitterProvider;

        public long? OutboxSequence => inner.OutboxSequence;

        public string? GatewayExecutionId => inner.GatewayExecutionId;

        public string? DecisionStage => inner.DecisionStage;

        public string? CapabilityGrantId => inner.CapabilityTokenId;

        public string? PolicyVersion => inner.PolicyVersion;

        public string? PolicyHash => inner.PolicyHash;

        public IReadOnlyDictionary<string, string> Metadata => inner.Metadata;
    }
}
