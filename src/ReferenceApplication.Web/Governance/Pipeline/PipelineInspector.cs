using System.Globalization;
using AsiBackbone.EntityFrameworkCore.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReferenceApplication.Infrastructure.Data;
using ReferenceApplication.Infrastructure.Data.Auditing;
using ReferenceApplication.Infrastructure.Data.Entities;
using ReferenceApplication.Web.Options;

namespace ReferenceApplication.Web.Governance.Pipeline;

/// <summary>
/// Reads the governance pipeline for the Development pipeline inspector.
/// </summary>
public interface IPipelineInspector
{
    /// <summary>
    /// Gets the names of the outbox statuses an inspector snapshot can filter by.
    /// </summary>
    IReadOnlyList<string> OutboxStatuses { get; }

    /// <summary>
    /// Reads the outbox, reconciliation, and telemetry, and optionally one request's stage timing.
    /// </summary>
    /// <param name="correlationId">The correlation identifier of a governed request to time, or <see langword="null" />.</param>
    /// <param name="outboxStatus">An outbox status to filter entries by, or <see langword="null" /> for all.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The snapshot. Nothing is written.</returns>
    ValueTask<PipelineInspectorSnapshot> GetSnapshotAsync(
        string? correlationId,
        string? outboxStatus,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the pipeline through the outbox query, the reconciler, the ledger, and the in-process telemetry listener.
/// </summary>
/// <remarks>
/// <para>
/// Every read is a query. Stage timing uses timestamps the pipeline already persists, plus the request's server span
/// when OpenTelemetry tracing created one, so collecting it adds nothing to the governed request path. The outbox and
/// the reconciler are read through their own query interfaces, so the inspector adds no second write path.
/// </para>
/// </remarks>
internal sealed class PipelineInspector(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    IApplicationAuditCompletionOutboxQuery outboxQuery,
    IApplicationAuditReconciler reconciler,
    IOptions<ApplicationAuditCompletionOutboxOptions> outboxOptions,
    IOptions<ApplicationAuditReconciliationOptions> reconciliationOptions,
    IOptions<ApplicationOpenTelemetryOptions> telemetryOptions,
    PipelineTelemetryRecorder telemetryRecorder,
    TimeProvider timeProvider) : IPipelineInspector
{
    private const string _aspNetCoreActivitySourceName = "Microsoft.AspNetCore";
    private const string _httpClientActivitySourceName = "System.Net.Http";
    private const string _routeTag = "http.route";
    private const string _methodTag = "http.request.method";

    // The meters OpenTelemetry's ASP.NET Core and HttpClient instrumentation add when the host enables them.
    private static readonly string[] _aspNetCoreMeterNames =
    [
        "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Server.Kestrel",
        "Microsoft.AspNetCore.Http.Connections",
        "Microsoft.AspNetCore.Routing",
        "Microsoft.AspNetCore.Diagnostics",
        "Microsoft.AspNetCore.RateLimiting"
    ];

    private static readonly string[] _httpClientMeterNames = ["System.Net.Http", "System.Net.NameResolution"];

    private static readonly TimeSpan _measurementWindow = TimeSpan.FromSeconds(2);

    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory =
        dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    private readonly IApplicationAuditCompletionOutboxQuery _outboxQuery =
        outboxQuery ?? throw new ArgumentNullException(nameof(outboxQuery));
    private readonly IApplicationAuditReconciler _reconciler =
        reconciler ?? throw new ArgumentNullException(nameof(reconciler));
    private readonly ApplicationAuditCompletionOutboxOptions _outboxOptions =
        outboxOptions?.Value ?? throw new ArgumentNullException(nameof(outboxOptions));
    private readonly ApplicationAuditReconciliationOptions _reconciliationOptions =
        reconciliationOptions?.Value ?? throw new ArgumentNullException(nameof(reconciliationOptions));
    private readonly ApplicationOpenTelemetryOptions _telemetryOptions =
        telemetryOptions?.Value ?? throw new ArgumentNullException(nameof(telemetryOptions));
    private readonly PipelineTelemetryRecorder _telemetryRecorder =
        telemetryRecorder ?? throw new ArgumentNullException(nameof(telemetryRecorder));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <inheritdoc />
    public IReadOnlyList<string> OutboxStatuses { get; } =
    [
        ApplicationAuditCompletionOutboxStatuses.Pending,
        ApplicationAuditCompletionOutboxStatuses.RetryableFailure,
        ApplicationAuditCompletionOutboxStatuses.Deferred,
        ApplicationAuditCompletionOutboxStatuses.Delivered,
        ApplicationAuditCompletionOutboxStatuses.Failed,
        ApplicationAuditCompletionOutboxStatuses.DeadLettered
    ];

    /// <inheritdoc />
    public async ValueTask<PipelineInspectorSnapshot> GetSnapshotAsync(
        string? correlationId,
        string? outboxStatus,
        CancellationToken cancellationToken = default)
    {
        string? status = string.IsNullOrWhiteSpace(outboxStatus) ? null : outboxStatus.Trim();
        if (status is not null && !OutboxStatuses.Contains(status, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{status}' is not an outbox status.", nameof(outboxStatus));
        }

        await using ApplicationDbContext dbContext = await _dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        PipelineOutboxView outbox = await ReadOutboxAsync(dbContext, status, cancellationToken).ConfigureAwait(false);
        PipelineReconciliationView reconciliation = await ReadReconciliationAsync(cancellationToken).ConfigureAwait(false);
        PipelineRequestView? request = string.IsNullOrWhiteSpace(correlationId)
            ? null
            : await ReadRequestAsync(dbContext, correlationId.Trim(), cancellationToken).ConfigureAwait(false);

        return new PipelineInspectorSnapshot(
            _timeProvider.GetUtcNow(),
            outbox,
            reconciliation,
            ReadTelemetry(),
            request);
    }

    private async Task<PipelineOutboxView> ReadOutboxAsync(
        ApplicationDbContext dbContext,
        string? status,
        CancellationToken cancellationToken)
    {
        ApplicationAuditCompletionOutboxHealth health = await _outboxQuery
            .GetHealthAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<ApplicationAuditCompletionOutboxItem> items = await _outboxQuery
            .QueryAsync(
                new ApplicationAuditCompletionOutboxQueryRequest(
                    Status: status,
                    MaximumResults: PipelineInspectorOptions.ListSize),
                cancellationToken)
            .ConfigureAwait(false);

        var lifecycleEventIds = items.ToDictionary(
            static item => item.Id,
            static item => AsiBackboneAuditCompletionPublisher.CreateLifecycleEventId(item.IdempotencyKey));
        string[] eventIds = [.. lifecycleEventIds.Values];
        HashSet<string> appended = eventIds.Length == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(
                await dbContext.AsiBackboneDecisionReceiptLifecycleEvents
                    .AsNoTracking()
                    .Where(lifecycleEvent => eventIds.Contains(lifecycleEvent.EventId))
                    .Select(static lifecycleEvent => lifecycleEvent.EventId)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false),
                StringComparer.Ordinal);

        return new PipelineOutboxView(
            Enabled: health.Enabled,
            BacklogCount: health.BacklogCount,
            OldestPendingAgeSeconds: health.OldestPendingAge?.TotalSeconds,
            TotalRetryCount: health.TotalRetryCount,
            DeadLetterCount: health.DeadLetterCount,
            MaxRetryAttempts: _outboxOptions.MaxRetryAttempts,
            PollIntervalSeconds: _outboxOptions.PollInterval.TotalSeconds,
            StatusFilter: status,
            Entries: [.. items.Select(item => new PipelineOutboxEntry(
                Id: item.Id,
                Status: item.Status,
                Destination: item.Destination,
                MutationBatchId: item.MutationBatchId,
                CorrelationId: item.CorrelationId,
                DecisionAuditRecordId: item.DecisionAuditRecordId,
                AuditRecordCount: item.AuditRecordCount,
                RetryCount: item.RetryCount,
                CreatedUtc: ToUtcOffset(item.CreatedUtc),
                LastAttemptUtc: ToUtcOffset(item.LastAttemptUtc),
                NextAttemptUtc: ToUtcOffset(item.NextAttemptUtc),
                DeliveredUtc: ToUtcOffset(item.DeliveredUtc),
                LastErrorCode: item.LastErrorCode,
                LastErrorMessage: item.LastErrorMessage,
                LifecycleEventId: lifecycleEventIds[item.Id],
                LifecycleEventAppended: appended.Contains(lifecycleEventIds[item.Id])))]);
    }

    private async Task<PipelineReconciliationView> ReadReconciliationAsync(CancellationToken cancellationToken)
    {
        ApplicationAuditReconciliationSummary summary = await _reconciler
            .GetSummaryAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<ApplicationAuditReconciliationFindingItem> findings = summary.Enabled
            ? await _reconciler
                .QueryFindingsAsync(
                    new ApplicationAuditReconciliationQuery(
                        RemediationStatus: ApplicationAuditReconciliationRemediationStatuses.Open,
                        MaximumResults: PipelineInspectorOptions.ListSize),
                    cancellationToken)
                .ConfigureAwait(false)
            : [];

        return new PipelineReconciliationView(
            Enabled: summary.Enabled,
            WorkerEnabled: _reconciliationOptions.Enabled && _reconciliationOptions.RunWorker,
            LastRunUtc: ToUtcOffset(summary.LastRunUtc),
            IntervalSeconds: _reconciliationOptions.Interval.TotalSeconds,
            OpenFindingCount: summary.OpenFindingCount,
            ErrorFindingCount: summary.ErrorFindingCount,
            CriticalFindingCount: summary.CriticalFindingCount,
            ManifestVerificationFailureCount: summary.ManifestVerificationFailureCount,
            MissingCompletionCount: summary.MissingCompletionCount,
            StaleDeliveryCount: summary.StaleDeliveryCount,
            DeadLetterCount: summary.DeadLetterCount,
            OpenFindings: [.. findings
                .OrderByDescending(static finding => finding.LastObservedUtc)
                .Select(static finding => new PipelineReconciliationFinding(
                    Id: finding.Id,
                    ReasonCode: finding.ReasonCode,
                    Severity: finding.Severity,
                    MutationBatchId: finding.MutationBatchId,
                    Destination: finding.Destination,
                    Guidance: finding.Guidance,
                    FirstObservedUtc: ToUtcOffset(finding.FirstObservedUtc),
                    LastObservedUtc: ToUtcOffset(finding.LastObservedUtc)))]);
    }

    private PipelineTelemetryView ReadTelemetry()
    {
        return new PipelineTelemetryView(
            TracingEnabled: _telemetryOptions.Enabled && _telemetryOptions.EnableTracing,
            MetricsEnabled: _telemetryOptions.Enabled && _telemetryOptions.EnableMetrics,
            OtlpExportEnabled: _telemetryOptions.Enabled && _telemetryOptions.Otlp.Enabled,
            ActivitySources: _telemetryRecorder.GetActivitySources(IsActivitySourceCollected),
            Meters: _telemetryRecorder.GetMeters(IsMeterCollected),
            HostGauges: _telemetryRecorder.ReadHostGauges());
    }

    private bool IsActivitySourceCollected(string name)
    {
        bool tracing = _telemetryOptions.Enabled && _telemetryOptions.EnableTracing;
        return tracing && (
            (_telemetryOptions.EnableAspNetCoreInstrumentation &&
                string.Equals(name, _aspNetCoreActivitySourceName, StringComparison.Ordinal)) ||
            (_telemetryOptions.EnableHttpClientInstrumentation &&
                string.Equals(name, _httpClientActivitySourceName, StringComparison.Ordinal)));
    }

    private bool IsMeterCollected(string name)
    {
        bool metrics = _telemetryOptions.Enabled && _telemetryOptions.EnableMetrics;
        return metrics && (
            (_telemetryOptions.EnableAspNetCoreInstrumentation &&
                _aspNetCoreMeterNames.Contains(name, StringComparer.Ordinal)) ||
            (_telemetryOptions.EnableHttpClientInstrumentation &&
                _httpClientMeterNames.Contains(name, StringComparer.Ordinal)));
    }

    private async Task<PipelineRequestView> ReadRequestAsync(
        ApplicationDbContext dbContext,
        string correlationId,
        CancellationToken cancellationToken)
    {
        AuditLedgerRecordEntity? decision = (await dbContext.AsiBackboneAuditLedgerRecords
                .AsNoTracking()
                .Where(record => record.CorrelationId == correlationId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .OrderBy(static record => record.RecordedUtc)
            .FirstOrDefault();

        if (decision is null)
        {
            return new PipelineRequestView(correlationId, TraceId: null, Found: false, [], [], []);
        }

        string decisionRecordId = decision.RecordId;
        var mutationAudits = await dbContext.AuditRecords
            .AsNoTracking()
            .Where(record => record.CorrelationId == correlationId || record.DecisionAuditRecordId == decisionRecordId)
            .Select(static record => new { record.ModifiedOnUtc, record.SpanId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        DateTime? mutationSavedUtc = mutationAudits.Count == 0 ? null : mutationAudits.Min(static audit => audit.ModifiedOnUtc);
        ApplicationAuditCompletionOutboxEntry? completion = (await dbContext.ApplicationAuditCompletionOutboxEntries
                .AsNoTracking()
                .Where(entry => entry.CorrelationId == correlationId || entry.DecisionAuditRecordId == decisionRecordId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .OrderBy(static entry => entry.CreatedUtc)
            .FirstOrDefault();
        string? lifecycleEventId = completion is null
            ? null
            : AsiBackboneAuditCompletionPublisher.CreateLifecycleEventId(completion.IdempotencyKey);
        bool lifecycleAppended = lifecycleEventId is not null && await dbContext.AsiBackboneDecisionReceiptLifecycleEvents
            .AsNoTracking()
            .AnyAsync(lifecycleEvent => lifecycleEvent.EventId == lifecycleEventId, cancellationToken)
            .ConfigureAwait(false);

        // A decision returned by a policy evaluator carries no trace identifier, because AsiBackbone's evaluation
        // context has none. The mutation audit records the span that saved the change, so its trace is looked up there.
        string? traceId = ParseTraceId(decision.TraceId) ?? mutationAudits
            .Select(static audit => audit.SpanId)
            .OfType<string>()
            .Select(_telemetryRecorder.FindTraceId)
            .OfType<string>()
            .FirstOrDefault();
        IReadOnlyList<PipelineSpan> spans = traceId is null ? [] : _telemetryRecorder.GetSpans(traceId);
        PipelineSpan? serverSpan = spans.FirstOrDefault(static span =>
            string.Equals(span.Kind, "Server", StringComparison.Ordinal) &&
            string.Equals(span.Source, _aspNetCoreActivitySourceName, StringComparison.Ordinal));

        List<PipelineStage> stages = CreateStages(
            decision,
            mutationSavedUtc is null ? null : ToUtcOffset(mutationSavedUtc.Value),
            completion,
            lifecycleAppended,
            serverSpan);

        return new PipelineRequestView(
            correlationId,
            traceId,
            Found: true,
            stages,
            spans,
            FindRequestMeasurements(serverSpan));
    }

    private List<PipelineStage> CreateStages(
        AuditLedgerRecordEntity decision,
        DateTimeOffset? mutationSavedUtc,
        ApplicationAuditCompletionOutboxEntry? completion,
        bool lifecycleAppended,
        PipelineSpan? serverSpan)
    {
        const string spanSource = "ASP.NET Core server span, seen by the in-process listener";
        const string noSpanNote =
            "No server span was linked. OpenTelemetry tracing is off, the span had not ended when this was read, or the " +
            "decision has neither a trace identifier nor a mutation audit to link it through.";
        const string noMutationNote = "This operation changes no data, so it stages no audit-completion entry.";

        List<StageDraft> drafts =
        [
            serverSpan is null
                ? new StageDraft("request-received", "Request received", true, PipelineStageStatuses.NotCaptured, null, spanSource, noSpanNote)
                : new StageDraft("request-received", "Request received", true, PipelineStageStatuses.Recorded, serverSpan.StartUtc, spanSource, null),
            new StageDraft(
                "decision-evaluated",
                "Decision evaluated",
                true,
                PipelineStageStatuses.Recorded,
                decision.OccurredUtc,
                "Decision ledger: OccurredUtc",
                "In-memory policy evaluation."),
            new StageDraft(
                "receipt-appended",
                "Decision receipt appended",
                true,
                PipelineStageStatuses.Recorded,
                decision.RecordedUtc,
                "Decision ledger: RecordedUtc",
                "The synchronous ledger append, before the action runs.")
        ];

        if (completion is null && mutationSavedUtc is null)
        {
            drafts.Add(new StageDraft("mutation-saved", "Mutation saved", true, PipelineStageStatuses.NotApplicable, null, "Mutation audit", noMutationNote));
            drafts.Add(new StageDraft("outbox-staged", "Outbox entry staged", true, PipelineStageStatuses.NotApplicable, null, "Audit-completion outbox", noMutationNote));
        }
        else
        {
            drafts.Add(new StageDraft(
                "mutation-saved",
                "Mutation saved",
                true,
                PipelineStageStatuses.Recorded,
                completion is null ? mutationSavedUtc : ToUtcOffset(completion.ReceiptCompletedUtc),
                completion is null ? "Mutation audit: ModifiedOnUtc" : "Outbox: ReceiptCompletedUtc",
                "The mutation and its audit records are saved inside the transaction."));
            drafts.Add(completion is null
                ? new StageDraft(
                    "outbox-staged",
                    "Outbox entry staged",
                    true,
                    PipelineStageStatuses.NotCaptured,
                    null,
                    "Audit-completion outbox",
                    "No outbox entry was staged: the audit-completion outbox is disabled.")
                : new StageDraft(
                    "outbox-staged",
                    "Outbox entry staged",
                    true,
                    PipelineStageStatuses.Recorded,
                    ToUtcOffset(completion.CreatedUtc),
                    "Outbox: CreatedUtc",
                    "Committed in the same transaction as the mutation."));
        }

        drafts.Add(serverSpan is null
            ? new StageDraft("response-sent", "Response sent", true, PipelineStageStatuses.NotCaptured, null, spanSource, noSpanNote)
            : new StageDraft(
                "response-sent",
                "Response sent",
                true,
                PipelineStageStatuses.Recorded,
                serverSpan.StartUtc.AddMilliseconds(serverSpan.DurationMilliseconds),
                spanSource,
                "The end of the synchronous path. Everything after this runs in the background."));

        drafts.AddRange(CreateAsynchronousStages(completion, lifecycleAppended));

        // Min skips stages without a time, and is null when no stage has one.
        DateTimeOffset? origin = drafts.Min(static draft => draft.OccurredUtc);

        return [.. drafts.Select(draft => new PipelineStage(
            draft.Key,
            draft.Name,
            draft.Synchronous,
            draft.Status,
            draft.OccurredUtc,
            draft.OccurredUtc is { } occurred && origin is { } first ? (occurred - first).TotalMilliseconds : null,
            draft.Source,
            draft.Note))];
    }

    private List<StageDraft> CreateAsynchronousStages(
        ApplicationAuditCompletionOutboxEntry? completion,
        bool lifecycleAppended)
    {
        const string dispatchSource = "Outbox: DeliveredUtc, or LastAttemptUtc while retrying";
        const string lifecycleSource = "Lifecycle table; the event carries the completion time, so the append is timed by its delivery";

        if (completion is null)
        {
            return
            [
                new StageDraft("outbox-dispatched", "Outbox dispatched", false, PipelineStageStatuses.NotApplicable, null, dispatchSource, null),
                new StageDraft("lifecycle-appended", "Lifecycle event appended", false, PipelineStageStatuses.NotApplicable, null, lifecycleSource, null)
            ];
        }

        string attempts = string.Create(
            CultureInfo.InvariantCulture,
            $"Attempt {completion.RetryCount} of {_outboxOptions.MaxRetryAttempts} failed");
        string polling = string.Create(
            CultureInfo.InvariantCulture,
            $"the background worker, which polls every {_outboxOptions.PollInterval.TotalSeconds:0.#} s");
        StageDraft dispatched = completion.Status switch
        {
            ApplicationAuditCompletionOutboxStatuses.Delivered => new StageDraft(
                "outbox-dispatched",
                "Outbox dispatched",
                false,
                PipelineStageStatuses.Recorded,
                ToUtcOffset(completion.DeliveredUtc),
                dispatchSource,
                $"Delivered by {polling}."),
            ApplicationAuditCompletionOutboxStatuses.RetryableFailure or ApplicationAuditCompletionOutboxStatuses.Deferred => new StageDraft(
                "outbox-dispatched",
                "Outbox dispatched",
                false,
                PipelineStageStatuses.Retrying,
                ToUtcOffset(completion.LastAttemptUtc),
                dispatchSource,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{attempts} ({completion.LastErrorCode ?? "no error code"}); next attempt at {ToUtcOffset(completion.NextAttemptUtc):O}.")),
            ApplicationAuditCompletionOutboxStatuses.Failed or ApplicationAuditCompletionOutboxStatuses.DeadLettered => new StageDraft(
                "outbox-dispatched",
                "Outbox dispatched",
                false,
                PipelineStageStatuses.Failed,
                ToUtcOffset(completion.LastAttemptUtc),
                dispatchSource,
                $"{completion.Status} ({completion.LastErrorCode ?? "no error code"}); delivery will not be retried."),
            _ => new StageDraft(
                "outbox-dispatched",
                "Outbox dispatched",
                false,
                PipelineStageStatuses.Pending,
                null,
                dispatchSource,
                $"Waiting for {polling}.")
        };

        StageDraft lifecycle = lifecycleAppended
            ? new StageDraft(
                "lifecycle-appended",
                "Lifecycle event appended",
                false,
                PipelineStageStatuses.Recorded,
                ToUtcOffset(completion.DeliveredUtc),
                lifecycleSource,
                "Appended by the delivery that dispatched the entry.")
            : new StageDraft(
                "lifecycle-appended",
                "Lifecycle event appended",
                false,
                PipelineStageStatuses.Pending,
                null,
                lifecycleSource,
                "Appended when the outbox entry is delivered.");

        return [dispatched, lifecycle];
    }

    private IReadOnlyList<PipelineMeasurement> FindRequestMeasurements(PipelineSpan? serverSpan)
    {
        if (serverSpan is null ||
            !serverSpan.Tags.TryGetValue(_routeTag, out string? route) ||
            !serverSpan.Tags.TryGetValue(_methodTag, out string? method))
        {
            return [];
        }

        // Metric measurements carry no trace identifier, so the request's duration measurement is matched by route,
        // method, and the time it was recorded.
        DateTimeOffset end = serverSpan.StartUtc.AddMilliseconds(serverSpan.DurationMilliseconds);
        PipelineMeasurement? match = _telemetryRecorder
            .GetMeasurements(PipelineTelemetryRecorder.AspNetCoreHostingMeterName, PipelineTelemetryRecorder.RequestDurationInstrumentName)
            .Where(measurement =>
                measurement.RecordedUtc >= serverSpan.StartUtc &&
                measurement.RecordedUtc <= end + _measurementWindow &&
                measurement.Tags.TryGetValue(_routeTag, out string? measuredRoute) &&
                string.Equals(measuredRoute, route, StringComparison.Ordinal) &&
                measurement.Tags.TryGetValue(_methodTag, out string? measuredMethod) &&
                string.Equals(measuredMethod, method, StringComparison.Ordinal))
            .OrderBy(measurement => (measurement.RecordedUtc - end).Duration())
            .FirstOrDefault();

        return match is null ? [] : [match];
    }

    private static string? ParseTraceId(string? recordedTraceId)
    {
        // The ledger records the W3C activity identifier (version-traceid-spanid-flags); a bare 32-character trace
        // identifier is accepted too. Anything else, such as the HttpContext.TraceIdentifier fallback, has no span.
        if (string.IsNullOrWhiteSpace(recordedTraceId))
        {
            return null;
        }

        string trimmed = recordedTraceId.Trim();
        string[] parts = trimmed.Split('-');
        string candidate = parts.Length == 4 ? parts[1] : trimmed;
        return candidate.Length == 32 && candidate.All(char.IsAsciiHexDigit) ? candidate : null;
    }

    private static DateTimeOffset ToUtcOffset(DateTime value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private static DateTimeOffset? ToUtcOffset(DateTime? value)
    {
        return value is null ? null : ToUtcOffset(value.Value);
    }

    private sealed record StageDraft(
        string Key,
        string Name,
        bool Synchronous,
        string Status,
        DateTimeOffset? OccurredUtc,
        string Source,
        string? Note);
}
