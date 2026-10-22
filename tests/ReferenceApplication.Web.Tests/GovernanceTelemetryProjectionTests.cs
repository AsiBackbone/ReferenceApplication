using AsiBackbone.Core.Actors;
using AsiBackbone.Core.Audit;
using AsiBackbone.Core.Decisions;
using AsiBackbone.Core.Emissions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceApplication.Infrastructure.Data;
using ReferenceApplication.Web.Governance;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies <see cref="GovernanceTelemetryProjection" />: persisted governance evidence is projected as a minimized
/// envelope that carries no actor, reason, metadata, or payload, a projection never throws or follows the request's
/// cancellation, and nothing is projected when governance telemetry is off or when evidence is not persisted.
/// </summary>
public sealed class GovernanceTelemetryProjectionTests
{
    /// <summary>
    /// Verifies that the disabled projection does nothing.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Disabled_ProjectsNothing()
    {
        Assert.False(GovernanceTelemetryProjection.Disabled.IsEnabled);

        await GovernanceTelemetryProjection.Disabled.ProjectDecisionAsync(CreateReceipt());
        await GovernanceTelemetryProjection.Disabled.ProjectLifecycleEventAsync(CreateLifecycleEvent());
    }

    /// <summary>
    /// Verifies that a decision is projected with its identifiers, policy identity, operation, and outcome, and without
    /// the actor, reason codes, or receipt metadata.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task ProjectDecisionAsync_EmitsMinimizedDecisionEnvelope()
    {
        CapturingEmitter emitter = new();
        GovernanceTelemetryProjection projection = new(NullLogger<GovernanceTelemetryProjection>.Instance, emitter);
        DecisionReceipt receipt = CreateReceipt();

        await projection.ProjectDecisionAsync(receipt);

        Assert.True(projection.IsEnabled);
        GovernanceEmissionEnvelope envelope = Assert.Single(emitter.Envelopes);
        Assert.Equal(GovernanceEmissionEventType.Decision, envelope.EventType);
        Assert.Equal(receipt.EventId, envelope.EventId);
        Assert.Equal(receipt.OccurredUtc, envelope.OccurredUtc);
        Assert.Equal("correlation-1", envelope.CorrelationId);
        Assert.Equal(receipt.DecisionReceiptId, envelope.DecisionReceiptId);
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", envelope.TraceId);
        Assert.Equal("1.0.0", envelope.PolicyVersion);
        Assert.Equal("sha256:test", envelope.PolicyHash);
        Assert.Equal("telemetry.test.operation", envelope.OperationName);
        Assert.Equal(receipt.Outcome, envelope.Outcome);

        Assert.Null(envelope.ActorId);
        Assert.Null(envelope.Payload);
        Assert.Empty(envelope.Metadata);
    }

    /// <summary>
    /// Verifies that a lifecycle event is projected with its stage and identifiers, and without its metadata, which
    /// carries mutation batch identifiers and manifest hashes.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task ProjectLifecycleEventAsync_EmitsMinimizedLifecycleEnvelope()
    {
        CapturingEmitter emitter = new();
        GovernanceTelemetryProjection projection = new(NullLogger<GovernanceTelemetryProjection>.Instance, emitter);
        DecisionReceiptLifecycleEvent lifecycleEvent = CreateLifecycleEvent();

        await projection.ProjectLifecycleEventAsync(lifecycleEvent);

        GovernanceEmissionEnvelope envelope = Assert.Single(emitter.Envelopes);
        Assert.Equal(GovernanceEmissionEventType.AuditLifecycle, envelope.EventType);
        Assert.Equal(lifecycleEvent.EventId, envelope.EventId);
        Assert.Equal(DecisionReceiptLifecycleStage.GatewayExecutionCompleted, envelope.LifecycleStage);
        Assert.Equal("correlation-1", envelope.CorrelationId);
        Assert.Equal("receipt-1", envelope.DecisionReceiptId);
        Assert.Equal("telemetry.test.operation", envelope.OperationName);
        Assert.Equal("Allowed", envelope.Outcome);

        Assert.Null(envelope.ActorId);
        Assert.Null(envelope.Payload);
        Assert.Empty(envelope.Metadata);
    }

    /// <summary>
    /// Verifies that a projection is never cancelled by the caller and never throws, whether the emitter reports a
    /// failure or throws.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Projection_EmitterFailsOrThrows_NeverThrowsAndIgnoresCancellation()
    {
        CapturingEmitter failing = new(GovernanceEmissionResult.RetryableFailure(
            GovernanceEmissionError.Create("test.failure", "The test emitter failed.", isRetryable: true)));
        await new GovernanceTelemetryProjection(NullLogger<GovernanceTelemetryProjection>.Instance, failing)
            .ProjectDecisionAsync(CreateReceipt());

        _ = Assert.Single(failing.Envelopes);
        Assert.False(Assert.Single(failing.CancellationTokens).CanBeCanceled);

        CapturingEmitter throwing = new(exception: new InvalidOperationException("The test emitter threw."));
        GovernanceTelemetryProjection projection = new(NullLogger<GovernanceTelemetryProjection>.Instance, throwing);
        await projection.ProjectDecisionAsync(CreateReceipt());
        await projection.ProjectLifecycleEventAsync(CreateLifecycleEvent());

        Assert.Equal(2, throwing.Envelopes.Count);
    }

    /// <summary>
    /// Verifies that the ledger sink projects nothing when the receipt cannot be persisted.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task LedgerSink_AppendFails_ProjectsNothing()
    {
        CapturingEmitter emitter = new();
        var sink = new LedgerDecisionReceiptSink(
            new ThrowingDbContextFactory(),
            new GovernanceDecisionEvidenceAccessor(),
            NullLoggerFactory.Instance,
            new GovernanceEvidenceNotificationPublisher(
                new NoopGovernanceEvidenceNotifier(),
                NullLogger<GovernanceEvidenceNotificationPublisher>.Instance),
            new GovernanceTelemetryProjection(NullLogger<GovernanceTelemetryProjection>.Instance, emitter));

        _ = await Assert.ThrowsAsync<GovernanceEvidencePersistenceException>(async () =>
            await sink.WriteAsync(CreateReceipt(), TestContext.Current.CancellationToken));

        Assert.Empty(emitter.Envelopes);
    }

    /// <summary>
    /// Verifies argument validation.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Projection_NullArguments_Throw()
    {
        GovernanceTelemetryProjection projection = new(NullLogger<GovernanceTelemetryProjection>.Instance, new CapturingEmitter());

        _ = Assert.Throws<ArgumentNullException>(() => new GovernanceTelemetryProjection(null!));
        _ = await Assert.ThrowsAsync<ArgumentNullException>(async () => await projection.ProjectDecisionAsync(null!));
        _ = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await projection.ProjectLifecycleEventAsync(null!));
    }

    private static DecisionReceipt CreateReceipt()
    {
        return DecisionReceipt.FromDecision(
            GovernanceActorContext.Human("user-123", "Test User"),
            "telemetry.test.operation",
            GovernanceDecision.Deny(
                "test.denied",
                "Denied by a test.",
                correlationId: "correlation-1",
                traceId: "0af7651916cd43dd8448eb211c80319c",
                policyVersion: "1.0.0",
                policyHash: "sha256:test"),
            metadata: new Dictionary<string, string>(StringComparer.Ordinal) { ["endpoint.test"] = "value" });
    }

    private static DecisionReceiptLifecycleEvent CreateLifecycleEvent()
    {
        return DecisionReceiptLifecycleEvent.Create(
            DecisionReceiptLifecycleStage.GatewayExecutionCompleted,
            "correlation-1",
            decisionReceiptId: "receipt-1",
            traceId: "0af7651916cd43dd8448eb211c80319c",
            operationName: "telemetry.test.operation",
            outcome: "Allowed",
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mutationBatchId"] = "batch-1",
                ["mutationManifestHash"] = "sha256:manifest"
            });
    }

    private sealed class CapturingEmitter(
        GovernanceEmissionResult? result = null,
        Exception? exception = null) : IGovernanceEmitter
    {
        public List<GovernanceEmissionEnvelope> Envelopes { get; } = [];

        public List<CancellationToken> CancellationTokens { get; } = [];

        public ValueTask<GovernanceEmissionResult> EmitAsync(
            GovernanceEmissionEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            Envelopes.Add(envelope);
            CancellationTokens.Add(cancellationToken);

            return exception is not null
                ? throw exception
                : ValueTask.FromResult(result ?? GovernanceEmissionResult.Delivered("test", envelope.EnvelopeId));
        }
    }

    private sealed class ThrowingDbContextFactory : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext()
        {
            throw new InvalidOperationException("The database cannot be reached.");
        }
    }

    private sealed class NoopGovernanceEvidenceNotifier : IGovernanceEvidenceNotifier
    {
        public void Publish(GovernanceEvidenceNotification notification)
        {
        }

        public IGovernanceEvidenceSubscription Subscribe()
        {
            throw new NotSupportedException();
        }
    }
}
