namespace ReferenceApplication.Web.Governance.Pipeline;

/// <summary>
/// Stable status values for a pipeline stage.
/// </summary>
public static class PipelineStageStatuses
{
    /// <summary>The stage happened, and its time comes from a persisted or captured timestamp.</summary>
    public const string Recorded = "recorded";

    /// <summary>The stage has not happened yet.</summary>
    public const string Pending = "pending";

    /// <summary>The stage was attempted and is waiting for a retry.</summary>
    public const string Retrying = "retrying";

    /// <summary>The stage failed and will not be retried.</summary>
    public const string Failed = "failed";

    /// <summary>The stage does not apply to this request.</summary>
    public const string NotApplicable = "not-applicable";

    /// <summary>The stage happened, but nothing recorded its time.</summary>
    public const string NotCaptured = "not-captured";
}

/// <summary>
/// One read of the governance pipeline: the outbox, reconciliation, telemetry, and optionally one request.
/// </summary>
/// <param name="GeneratedUtc">When the snapshot was read.</param>
/// <param name="Outbox">The audit-completion outbox.</param>
/// <param name="Reconciliation">The latest reconciliation summary and its open findings.</param>
/// <param name="Telemetry">The telemetry sources and meters present in the process.</param>
/// <param name="Request">The selected request's stage timing and telemetry, when one was asked for.</param>
public sealed record PipelineInspectorSnapshot(
    DateTimeOffset GeneratedUtc,
    PipelineOutboxView Outbox,
    PipelineReconciliationView Reconciliation,
    PipelineTelemetryView Telemetry,
    PipelineRequestView? Request);

/// <summary>
/// The audit-completion outbox: its health and its most recent entries.
/// </summary>
/// <param name="Enabled">Whether the outbox is enabled.</param>
/// <param name="BacklogCount">Entries waiting for delivery: pending, retrying, or deferred.</param>
/// <param name="OldestPendingAgeSeconds">The age of the oldest waiting entry, when there is one.</param>
/// <param name="TotalRetryCount">Delivery retries across all entries.</param>
/// <param name="DeadLetterCount">Entries that exhausted their retries.</param>
/// <param name="MaxRetryAttempts">The attempts a retrying entry gets before it is dead-lettered.</param>
/// <param name="PollIntervalSeconds">How often the delivery worker polls for ready entries.</param>
/// <param name="StatusFilter">The status the entries were filtered by, when one was given.</param>
/// <param name="Entries">The most recent entries, newest first.</param>
public sealed record PipelineOutboxView(
    bool Enabled,
    long BacklogCount,
    double? OldestPendingAgeSeconds,
    long TotalRetryCount,
    long DeadLetterCount,
    int MaxRetryAttempts,
    double PollIntervalSeconds,
    string? StatusFilter,
    IReadOnlyList<PipelineOutboxEntry> Entries);

/// <summary>
/// One audit-completion outbox entry and the lifecycle event its delivery produces.
/// </summary>
/// <param name="Id">The entry identifier.</param>
/// <param name="Status">The delivery status.</param>
/// <param name="Destination">The delivery destination.</param>
/// <param name="MutationBatchId">The mutation batch the entry completes.</param>
/// <param name="CorrelationId">The originating request's correlation identifier.</param>
/// <param name="DecisionAuditRecordId">The governance decision the mutation was made under.</param>
/// <param name="AuditRecordCount">The mutation-audit records in the batch.</param>
/// <param name="RetryCount">The failed delivery attempts so far.</param>
/// <param name="CreatedUtc">When the entry was staged.</param>
/// <param name="LastAttemptUtc">When delivery was last attempted.</param>
/// <param name="NextAttemptUtc">When delivery will next be attempted.</param>
/// <param name="DeliveredUtc">When the entry was delivered.</param>
/// <param name="LastErrorCode">The last delivery error code.</param>
/// <param name="LastErrorMessage">The last delivery error message.</param>
/// <param name="LifecycleEventId">The identifier of the lifecycle event delivery appends.</param>
/// <param name="LifecycleEventAppended">Whether that lifecycle event is in the lifecycle table.</param>
public sealed record PipelineOutboxEntry(
    Guid Id,
    string Status,
    string Destination,
    string MutationBatchId,
    string? CorrelationId,
    string? DecisionAuditRecordId,
    int AuditRecordCount,
    int RetryCount,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? NextAttemptUtc,
    DateTimeOffset? DeliveredUtc,
    string? LastErrorCode,
    string? LastErrorMessage,
    string LifecycleEventId,
    bool LifecycleEventAppended);

/// <summary>
/// The latest audit reconciliation summary and its open findings.
/// </summary>
/// <param name="Enabled">Whether reconciliation is enabled.</param>
/// <param name="WorkerEnabled">Whether the background reconciliation worker runs.</param>
/// <param name="LastRunUtc">When reconciliation last ran in this process.</param>
/// <param name="IntervalSeconds">How often the worker runs.</param>
/// <param name="OpenFindingCount">Open findings.</param>
/// <param name="ErrorFindingCount">Open findings with error severity.</param>
/// <param name="CriticalFindingCount">Open findings with critical severity.</param>
/// <param name="ManifestVerificationFailureCount">Open manifest-verification failures.</param>
/// <param name="MissingCompletionCount">Open missing-completion findings.</param>
/// <param name="StaleDeliveryCount">Open stale-delivery findings.</param>
/// <param name="DeadLetterCount">Open dead-letter findings.</param>
/// <param name="OpenFindings">The most recently observed open findings.</param>
public sealed record PipelineReconciliationView(
    bool Enabled,
    bool WorkerEnabled,
    DateTimeOffset? LastRunUtc,
    double IntervalSeconds,
    long OpenFindingCount,
    long ErrorFindingCount,
    long CriticalFindingCount,
    long ManifestVerificationFailureCount,
    long MissingCompletionCount,
    long StaleDeliveryCount,
    long DeadLetterCount,
    IReadOnlyList<PipelineReconciliationFinding> OpenFindings);

/// <summary>
/// One open reconciliation finding.
/// </summary>
/// <param name="Id">The finding identifier.</param>
/// <param name="ReasonCode">The finding reason code.</param>
/// <param name="Severity">The finding severity.</param>
/// <param name="MutationBatchId">The mutation batch the finding concerns.</param>
/// <param name="Destination">The delivery destination, when the finding concerns one.</param>
/// <param name="Guidance">What to do about it.</param>
/// <param name="FirstObservedUtc">When the finding was first observed.</param>
/// <param name="LastObservedUtc">When the finding was last observed.</param>
public sealed record PipelineReconciliationFinding(
    Guid Id,
    string ReasonCode,
    string Severity,
    string MutationBatchId,
    string? Destination,
    string Guidance,
    DateTimeOffset FirstObservedUtc,
    DateTimeOffset LastObservedUtc);

/// <summary>
/// The telemetry sources and meters present in the process, as an in-process listener sees them.
/// </summary>
/// <param name="TracingEnabled">Whether the host's OpenTelemetry tracing is enabled.</param>
/// <param name="MetricsEnabled">Whether the host's OpenTelemetry metrics are enabled.</param>
/// <param name="OtlpExportEnabled">Whether the host exports over OTLP.</param>
/// <param name="ActivitySources">The activity sources that exist in the process.</param>
/// <param name="Meters">The meters that exist in the process.</param>
/// <param name="HostGauges">The current values of the host's own observable gauges.</param>
public sealed record PipelineTelemetryView(
    bool TracingEnabled,
    bool MetricsEnabled,
    bool OtlpExportEnabled,
    IReadOnlyList<PipelineActivitySourceInfo> ActivitySources,
    IReadOnlyList<PipelineMeterInfo> Meters,
    IReadOnlyList<PipelineGaugeReading> HostGauges);

/// <summary>
/// An activity source that exists in the process.
/// </summary>
/// <param name="Name">The source name.</param>
/// <param name="Version">The source version.</param>
/// <param name="AddedToOpenTelemetry">Whether the host's OpenTelemetry configuration collects it.</param>
/// <param name="SpansObserved">The spans from it the listener has observed since the process started.</param>
public sealed record PipelineActivitySourceInfo(
    string Name,
    string? Version,
    bool AddedToOpenTelemetry,
    long SpansObserved);

/// <summary>
/// A meter that exists in the process.
/// </summary>
/// <param name="Name">The meter name.</param>
/// <param name="Version">The meter version.</param>
/// <param name="AddedToOpenTelemetry">Whether the host's OpenTelemetry configuration collects it.</param>
/// <param name="Instruments">Its instruments.</param>
public sealed record PipelineMeterInfo(
    string Name,
    string? Version,
    bool AddedToOpenTelemetry,
    IReadOnlyList<PipelineInstrumentInfo> Instruments);

/// <summary>
/// One instrument of a meter.
/// </summary>
/// <param name="Name">The instrument name.</param>
/// <param name="Kind">The instrument kind, such as <c>Histogram</c> or <c>ObservableGauge</c>.</param>
/// <param name="Unit">The instrument unit.</param>
public sealed record PipelineInstrumentInfo(string Name, string Kind, string? Unit);

/// <summary>
/// The current value of one of the host's own observable gauges.
/// </summary>
/// <param name="Meter">The meter name.</param>
/// <param name="Instrument">The instrument name.</param>
/// <param name="Value">The observed value.</param>
public sealed record PipelineGaugeReading(string Meter, string Instrument, double Value);

/// <summary>
/// One governed request's stage timing and the telemetry emitted for it.
/// </summary>
/// <param name="CorrelationId">The request correlation identifier.</param>
/// <param name="TraceId">The request's W3C trace identifier, when it is known.</param>
/// <param name="Found">Whether a governance decision is recorded for the correlation identifier.</param>
/// <param name="Stages">Each stage, in pipeline order; synchronous stages come first.</param>
/// <param name="Spans">The spans observed for the request's trace.</param>
/// <param name="Measurements">The metric measurements recorded for the request.</param>
public sealed record PipelineRequestView(
    string CorrelationId,
    string? TraceId,
    bool Found,
    IReadOnlyList<PipelineStage> Stages,
    IReadOnlyList<PipelineSpan> Spans,
    IReadOnlyList<PipelineMeasurement> Measurements);

/// <summary>
/// One stage of a governed request.
/// </summary>
/// <param name="Key">A stable key for the stage.</param>
/// <param name="Name">The stage, as a reader would name it.</param>
/// <param name="Synchronous">Whether the stage runs on the request path, before the response.</param>
/// <param name="Status">One of the <see cref="PipelineStageStatuses" /> values.</param>
/// <param name="OccurredUtc">When the stage happened, when it is known.</param>
/// <param name="OffsetMilliseconds">The time since the first recorded stage.</param>
/// <param name="Source">Where the timestamp comes from.</param>
/// <param name="Note">What a reader should know about this stage.</param>
public sealed record PipelineStage(
    string Key,
    string Name,
    bool Synchronous,
    string Status,
    DateTimeOffset? OccurredUtc,
    double? OffsetMilliseconds,
    string Source,
    string? Note);

/// <summary>
/// A span observed by the in-process listener.
/// </summary>
/// <param name="Source">The activity source name.</param>
/// <param name="Name">The span name.</param>
/// <param name="TraceId">The W3C trace identifier.</param>
/// <param name="SpanId">The span identifier.</param>
/// <param name="ParentSpanId">The parent span identifier, when there is one.</param>
/// <param name="Kind">The span kind.</param>
/// <param name="StartUtc">When the span started.</param>
/// <param name="DurationMilliseconds">How long the span lasted.</param>
/// <param name="Status">The span status.</param>
/// <param name="Tags">A small set of the span's tags.</param>
public sealed record PipelineSpan(
    string Source,
    string Name,
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    string Kind,
    DateTimeOffset StartUtc,
    double DurationMilliseconds,
    string Status,
    IReadOnlyDictionary<string, string> Tags);

/// <summary>
/// A metric measurement observed by the in-process listener.
/// </summary>
/// <param name="Meter">The meter name.</param>
/// <param name="Instrument">The instrument name.</param>
/// <param name="Value">The measured value.</param>
/// <param name="Unit">The instrument unit.</param>
/// <param name="RecordedUtc">When the measurement was recorded.</param>
/// <param name="Tags">A small set of the measurement's tags.</param>
public sealed record PipelineMeasurement(
    string Meter,
    string Instrument,
    double Value,
    string? Unit,
    DateTimeOffset RecordedUtc,
    IReadOnlyDictionary<string, string> Tags);
