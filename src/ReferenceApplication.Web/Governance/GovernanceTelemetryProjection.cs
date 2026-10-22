using AsiBackbone.Core.Audit;
using AsiBackbone.Core.Emissions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Projects persisted governance evidence onto the host's telemetry through AsiBackbone's governance emitter.
/// </summary>
/// <remarks>
/// <para>
/// The AsiBackbone audit ledger and lifecycle store remain the record of governance; telemetry is a projection of it.
/// <see cref="LedgerDecisionReceiptSink" /> projects a decision only after its receipt is appended, and
/// <see cref="AsiBackboneAuditCompletionPublisher" /> projects a lifecycle event only after it is appended, so nothing
/// reaches telemetry that the ledger does not hold. A projection is best effort: it never throws, is never cancelled by
/// the governed request, and cannot change a governance outcome. When the host registers no
/// <see cref="IGovernanceEmitter" />, as when OpenTelemetry or governance instrumentation is disabled, projection does
/// nothing.
/// </para>
/// <para>
/// Envelopes are built field by field, so only identifiers, policy identity, the operation name, the outcome, and the
/// decision or lifecycle stage leave the process. The actor, reason codes, receipt and lifecycle metadata (including
/// mutation batch identifiers and manifest hashes), request bodies, and mutation values are never projected, whatever
/// emitter is registered.
/// </para>
/// </remarks>
public sealed partial class GovernanceTelemetryProjection
{
    private readonly ILogger<GovernanceTelemetryProjection> _logger;
    private readonly IGovernanceEmitter? _emitter;

    /// <summary>
    /// Initializes a new instance of the <see cref="GovernanceTelemetryProjection" /> class.
    /// </summary>
    /// <param name="logger">The logger used when a projection is not delivered.</param>
    /// <param name="emitter">The governance emitter, or <see langword="null" /> when governance telemetry is off.</param>
    public GovernanceTelemetryProjection(
        ILogger<GovernanceTelemetryProjection> logger,
        IGovernanceEmitter? emitter = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _emitter = emitter;
    }

    /// <summary>
    /// Gets a projection that does nothing, for hosts and tests without governance telemetry.
    /// </summary>
    public static GovernanceTelemetryProjection Disabled { get; } =
        new(NullLogger<GovernanceTelemetryProjection>.Instance);

    /// <summary>
    /// Gets a value indicating whether evidence is projected.
    /// </summary>
    public bool IsEnabled => _emitter is not null;

    /// <summary>
    /// Projects a decision whose receipt has been appended to the audit ledger.
    /// </summary>
    /// <param name="receipt">The persisted decision receipt.</param>
    /// <returns>A task that completes when the projection has been attempted.</returns>
    public ValueTask ProjectDecisionAsync(IDecisionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        return _emitter is null ? ValueTask.CompletedTask : EmitAsync(_emitter, CreateDecisionEnvelope(receipt));
    }

    /// <summary>
    /// Projects a lifecycle event that has been appended to the lifecycle store.
    /// </summary>
    /// <param name="lifecycleEvent">The persisted lifecycle event.</param>
    /// <returns>A task that completes when the projection has been attempted.</returns>
    public ValueTask ProjectLifecycleEventAsync(DecisionReceiptLifecycleEvent lifecycleEvent)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);

        return _emitter is null ? ValueTask.CompletedTask : EmitAsync(_emitter, CreateLifecycleEnvelope(lifecycleEvent));
    }

    /// <summary>
    /// Creates the minimized envelope for a decision.
    /// </summary>
    /// <param name="receipt">The decision receipt.</param>
    /// <returns>The envelope.</returns>
    internal static GovernanceEmissionEnvelope CreateDecisionEnvelope(IDecisionReceipt receipt)
    {
        return GovernanceEmissionEnvelope.Create(
            GovernanceEmissionEventType.Decision,
            eventId: receipt.EventId,
            occurredUtc: receipt.OccurredUtc,
            schemaVersion: receipt.SchemaVersion,
            correlationId: receipt.CorrelationId,
            decisionReceiptId: receipt.DecisionReceiptId,
            policyVersion: receipt.PolicyVersion,
            policyHash: receipt.PolicyHash,
            traceId: receipt.TraceId,
            spanId: receipt.SpanId,
            parentSpanId: receipt.ParentSpanId,
            operationName: receipt.OperationName,
            outcome: receipt.Outcome,
            decisionStage: receipt.DecisionStage);
    }

    /// <summary>
    /// Creates the minimized envelope for a lifecycle event.
    /// </summary>
    /// <param name="lifecycleEvent">The lifecycle event.</param>
    /// <returns>The envelope.</returns>
    internal static GovernanceEmissionEnvelope CreateLifecycleEnvelope(DecisionReceiptLifecycleEvent lifecycleEvent)
    {
        return GovernanceEmissionEnvelope.Create(
            GovernanceEmissionEventType.AuditLifecycle,
            eventId: lifecycleEvent.EventId,
            occurredUtc: lifecycleEvent.OccurredUtc,
            correlationId: lifecycleEvent.CorrelationId,
            decisionReceiptId: lifecycleEvent.DecisionReceiptId,
            lifecycleStage: lifecycleEvent.Stage,
            traceId: lifecycleEvent.TraceId,
            operationName: lifecycleEvent.OperationName,
            outcome: lifecycleEvent.Outcome);
    }

    private async ValueTask EmitAsync(IGovernanceEmitter emitter, GovernanceEmissionEnvelope envelope)
    {
        try
        {
            // The evidence is already persisted, so the request's cancellation must not abandon its projection.
            GovernanceEmissionResult result = await emitter
                .EmitAsync(envelope, CancellationToken.None)
                .ConfigureAwait(false);

            if (result.Status != GovernanceEmissionStatus.Delivered)
            {
                LogProjectionNotDelivered(
                    _logger,
                    envelope.EventType,
                    result.Status,
                    result.Error?.Code ?? "none");
            }
        }
        catch (Exception exception)
        {
            LogProjectionFailed(_logger, envelope.EventType, exception.GetType().Name, exception);
        }
    }

    [LoggerMessage(
        EventId = 26101,
        Level = LogLevel.Warning,
        Message = "Governance telemetry projection of a {EventType} event was not delivered ({Status}, {ErrorCode}). The persisted evidence is unaffected.")]
    private static partial void LogProjectionNotDelivered(
        ILogger logger,
        GovernanceEmissionEventType eventType,
        GovernanceEmissionStatus status,
        string errorCode);

    [LoggerMessage(
        EventId = 26102,
        Level = LogLevel.Warning,
        Message = "Governance telemetry projection of a {EventType} event failed ({ExceptionType}). The persisted evidence is unaffected.")]
    private static partial void LogProjectionFailed(
        ILogger logger,
        GovernanceEmissionEventType eventType,
        string exceptionType,
        Exception exception);
}
