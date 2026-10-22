using AsiBackbone.Core.Actors;
using AsiBackbone.Core.Audit;
using AsiBackbone.Core.Decisions;
using AsiBackbone.Core.HostIntegration;
using AsiBackbone.EntityFrameworkCore.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceApplication.Infrastructure.Data;
using ReferenceApplication.Infrastructure.Data.Auditing;
using ReferenceApplication.Web.Governance;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies <see cref="AsiBackboneAuditCompletionPublisher" />, the host-owned adapter that links NCAT audit completion
/// to AsiBackbone decision evidence (walkthrough Step 6).
/// </summary>
public sealed class AsiBackboneAuditCompletionPublisherTests
{
    private const string _correlationId = "correlation-1";
    private const string _traceId = "0af7651916cd43dd8448eb211c80319c";
    private const string _manifestHash = "ABCDEF0123456789";

    /// <summary>
    /// Verifies that a committed completion is delivered as a lifecycle event linked to the decision and carrying the
    /// mutation evidence.
    /// </summary>
    [Fact]
    public async Task PublishAsync_CommittedCompletion_AppendsLinkedLifecycleEvent()
    {
        await using TestApplicationServices app = await TestApplicationServices.CreateAsync();
        AuditLedgerRecord decision = await AppendDecisionAsync(app);
        ApplicationAuditCompletionMessage message = CreateMessage(decision.RecordId);

        ApplicationAuditCompletionPublishResult result = await PublishAsync(app, message);

        Assert.Equal(ApplicationAuditCompletionPublishDisposition.Delivered, result.Disposition);
        DecisionReceiptLifecycleEvent lifecycleEvent = Assert.IsType<DecisionReceiptLifecycleEvent>(
            await FindLifecycleEventAsync(app, AsiBackboneAuditCompletionPublisher.CreateLifecycleEventId(message.IdempotencyKey)));

        Assert.Equal(DecisionReceiptLifecycleStage.GatewayExecutionCompleted, lifecycleEvent.Stage);
        Assert.Equal(decision.CorrelationId, lifecycleEvent.CorrelationId);
        Assert.Equal(decision.TraceId, lifecycleEvent.TraceId);
        Assert.Equal(decision.OperationName, lifecycleEvent.OperationName);
        Assert.Equal(decision.RecordId, lifecycleEvent.Metadata[HostAccountabilityMetadataKeys.DecisionAuditRecordId]);
        Assert.Equal("execution-1", lifecycleEvent.Metadata[HostAccountabilityMetadataKeys.OperationExecutionId]);
        Assert.Equal("attempt-1", lifecycleEvent.Metadata[HostAccountabilityMetadataKeys.ExecutionAttemptId]);
        Assert.Equal("batch-1", lifecycleEvent.Metadata[HostAccountabilityMetadataKeys.MutationBatchId]);
        Assert.Equal("2", lifecycleEvent.Metadata[HostAccountabilityMetadataKeys.MutationRecordCount]);
        Assert.Equal(_manifestHash.ToLowerInvariant(), lifecycleEvent.Metadata[HostAccountabilityMetadataKeys.MutationManifestHash]);
        Assert.Equal("SHA-256", lifecycleEvent.Metadata[HostAccountabilityMetadataKeys.MutationManifestAlgorithm]);
        Assert.Equal(
            AsiBackboneAuditCompletionPublisher.PersistenceProviderName,
            lifecycleEvent.Metadata[HostAccountabilityMetadataKeys.PersistenceProvider]);
        Assert.Equal(message.IdempotencyKey, lifecycleEvent.Metadata[AsiBackboneAuditCompletionPublisher.CompletionEntryIdMetadataKey]);
    }

    /// <summary>
    /// Verifies at-least-once delivery: redelivering the same completion is acknowledged without a second event.
    /// </summary>
    [Fact]
    public async Task PublishAsync_Redelivery_IsAcknowledgedAsDuplicate()
    {
        await using TestApplicationServices app = await TestApplicationServices.CreateAsync();
        AuditLedgerRecord decision = await AppendDecisionAsync(app);
        ApplicationAuditCompletionMessage message = CreateMessage(decision.RecordId);

        _ = await PublishAsync(app, message);
        ApplicationAuditCompletionPublishResult redelivery = await PublishAsync(app, message);

        Assert.Equal(ApplicationAuditCompletionPublishDisposition.Delivered, redelivery.Disposition);
        Assert.Equal(1, await CountLifecycleEventsAsync(app));
    }

    /// <summary>
    /// Verifies that different evidence under an already-used idempotency key is a terminal conflict, not an overwrite.
    /// </summary>
    [Fact]
    public async Task PublishAsync_DifferentEvidenceUnderSameKey_IsTerminalConflict()
    {
        await using TestApplicationServices app = await TestApplicationServices.CreateAsync();
        AuditLedgerRecord decision = await AppendDecisionAsync(app);
        ApplicationAuditCompletionMessage message = CreateMessage(decision.RecordId);
        _ = await PublishAsync(app, message);

        ApplicationAuditCompletionPublishResult result = await PublishAsync(
            app,
            message with { MutationBatchId = "batch-2" });

        Assert.Equal(ApplicationAuditCompletionPublishDisposition.Failed, result.Disposition);
        Assert.Equal("idempotency-conflict", result.ErrorCode);
        Assert.Equal(1, await CountLifecycleEventsAsync(app));
    }

    /// <summary>
    /// Verifies that a completion whose decision record does not exist yet is deferred rather than delivered.
    /// </summary>
    [Fact]
    public async Task PublishAsync_DecisionNotYetPersisted_IsDeferred()
    {
        await using TestApplicationServices app = await TestApplicationServices.CreateAsync();

        ApplicationAuditCompletionPublishResult result = await PublishAsync(app, CreateMessage("missing-decision"));

        Assert.Equal(ApplicationAuditCompletionPublishDisposition.Deferred, result.Disposition);
        Assert.Equal("decision-receipt-not-available", result.ErrorCode);
        Assert.Equal(0, await CountLifecycleEventsAsync(app));
    }

    /// <summary>
    /// Verifies that a completion cannot be attached to a decision with different correlation or trace identifiers.
    /// </summary>
    /// <param name="correlationId">The message correlation identifier.</param>
    /// <param name="traceId">The message trace identifier.</param>
    /// <param name="expectedErrorCode">The expected terminal reason.</param>
    [Theory]
    [InlineData("other-correlation", _traceId, "correlation-id-mismatch")]
    [InlineData(_correlationId, "ffffffffffffffffffffffffffffffff", "trace-id-mismatch")]
    public async Task PublishAsync_CorrelationMismatch_IsTerminal(string correlationId, string traceId, string expectedErrorCode)
    {
        await using TestApplicationServices app = await TestApplicationServices.CreateAsync();
        AuditLedgerRecord decision = await AppendDecisionAsync(app);

        ApplicationAuditCompletionPublishResult result = await PublishAsync(
            app,
            CreateMessage(decision.RecordId) with { CorrelationId = correlationId, TraceId = traceId });

        Assert.Equal(ApplicationAuditCompletionPublishDisposition.Failed, result.Disposition);
        Assert.Equal(expectedErrorCode, result.ErrorCode);
    }

    /// <summary>
    /// Verifies that a completion without correlation identifiers is accepted: only a supplied identifier must match.
    /// </summary>
    [Fact]
    public async Task PublishAsync_WithoutCorrelationIdentifiers_IsDelivered()
    {
        await using TestApplicationServices app = await TestApplicationServices.CreateAsync();
        AuditLedgerRecord decision = await AppendDecisionAsync(app);

        ApplicationAuditCompletionPublishResult result = await PublishAsync(
            app,
            CreateMessage(decision.RecordId) with { CorrelationId = null, TraceId = " " });

        Assert.Equal(ApplicationAuditCompletionPublishDisposition.Delivered, result.Disposition);
    }

    /// <summary>
    /// Verifies the required-field validation, which needs no database access.
    /// </summary>
    /// <param name="field">The field to invalidate.</param>
    /// <param name="expectedErrorCode">The expected terminal reason.</param>
    [Theory]
    [InlineData("idempotency", "completion-entry-id-required")]
    [InlineData("operation", "operation-execution-id-required")]
    [InlineData("decision", "decision-audit-record-id-required")]
    [InlineData("outcome", "persistence-outcome-required")]
    [InlineData("count", "audit-record-count-invalid")]
    [InlineData("unknown-outcome", "unsupported-persistence-outcome")]
    public async Task PublishAsync_InvalidMessage_IsTerminal(string field, string expectedErrorCode)
    {
        await using TestApplicationServices app = await TestApplicationServices.CreateAsync(migrate: false);
        ApplicationAuditCompletionMessage valid = CreateMessage("decision-1");
        ApplicationAuditCompletionMessage message = field switch
        {
            "idempotency" => valid with { IdempotencyKey = " " },
            "operation" => valid with { OperationExecutionId = null },
            "decision" => valid with { DecisionAuditRecordId = "" },
            "outcome" => valid with { PersistenceOutcome = " " },
            "count" => valid with { AuditRecordCount = -1 },
            "unknown-outcome" => valid with { PersistenceOutcome = "partially-committed" },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
        };

        ApplicationAuditCompletionPublishResult result = await PublishAsync(app, message);

        Assert.Equal(ApplicationAuditCompletionPublishDisposition.Failed, result.Disposition);
        Assert.Equal(expectedErrorCode, result.ErrorCode);
    }

    /// <summary>
    /// Verifies that a committed completion without its mutation evidence is rejected rather than recorded.
    /// </summary>
    [Fact]
    public async Task PublishAsync_CommittedWithoutMutationEvidence_IsTerminal()
    {
        await using TestApplicationServices app = await TestApplicationServices.CreateAsync();
        AuditLedgerRecord decision = await AppendDecisionAsync(app);

        ApplicationAuditCompletionPublishResult result = await PublishAsync(
            app,
            CreateMessage(decision.RecordId) with { AuditRecordCount = 0 });

        Assert.Equal(ApplicationAuditCompletionPublishDisposition.Failed, result.Disposition);
        Assert.Equal("invalid-execution-receipt", result.ErrorCode);
        Assert.Equal(0, await CountLifecycleEventsAsync(app));
    }

    /// <summary>
    /// Verifies that NCAT outcome spellings are matched regardless of case and separators.
    /// </summary>
    /// <param name="outcome">The NCAT persistence outcome.</param>
    /// <param name="expected">The AsiBackbone persistence outcome.</param>
    [Theory]
    [InlineData("Committed", GovernedOperationPersistenceOutcome.Committed)]
    [InlineData("rolled-back", GovernedOperationPersistenceOutcome.RolledBack)]
    [InlineData("FAILED", GovernedOperationPersistenceOutcome.Failed)]
    [InlineData("no-mutation", GovernedOperationPersistenceOutcome.CompletedWithoutMutation)]
    [InlineData("completed_without_mutation", GovernedOperationPersistenceOutcome.CompletedWithoutMutation)]
    public async Task PublishAsync_MapsPersistenceOutcomes(string outcome, GovernedOperationPersistenceOutcome expected)
    {
        await using TestApplicationServices app = await TestApplicationServices.CreateAsync();
        AuditLedgerRecord decision = await AppendDecisionAsync(app);
        ApplicationAuditCompletionMessage message = expected == GovernedOperationPersistenceOutcome.Committed
            ? CreateMessage(decision.RecordId)
            : CreateMessage(decision.RecordId) with
            {
                MutationBatchId = string.Empty,
                AuditRecordCount = 0,
                MutationManifestHash = string.Empty,
                MutationManifestAlgorithm = string.Empty
            };

        ApplicationAuditCompletionPublishResult result = await PublishAsync(app, message with { PersistenceOutcome = outcome });

        Assert.Equal(ApplicationAuditCompletionPublishDisposition.Delivered, result.Disposition);
        DecisionReceiptLifecycleEvent lifecycleEvent = Assert.IsType<DecisionReceiptLifecycleEvent>(
            await FindLifecycleEventAsync(app, AsiBackboneAuditCompletionPublisher.CreateLifecycleEventId(message.IdempotencyKey)));
        Assert.Equal(
            GovernedOperationExecutionReceipt.Create("probe", expected, mutationBatchId: expected == GovernedOperationPersistenceOutcome.Committed ? "b" : null, mutationRecordCount: expected == GovernedOperationPersistenceOutcome.Committed ? 1 : 0, mutationManifestHash: expected == GovernedOperationPersistenceOutcome.Committed ? "aa" : null, mutationManifestAlgorithm: expected == GovernedOperationPersistenceOutcome.Committed ? "SHA-256" : null)
                .ToLifecycleMetadata()[HostAccountabilityMetadataKeys.PersistenceOutcome],
            lifecycleEvent.Metadata[HostAccountabilityMetadataKeys.PersistenceOutcome]);
    }

    /// <summary>
    /// Verifies the deterministic lifecycle event identifier.
    /// </summary>
    [Fact]
    public void CreateLifecycleEventId_IsDeterministicAndTrimmed()
    {
        string id = AsiBackboneAuditCompletionPublisher.CreateLifecycleEventId("completion-key");

        Assert.StartsWith(AsiBackboneAuditCompletionPublisher.LifecycleEventIdPrefix, id, StringComparison.Ordinal);
        Assert.Matches("^ncat-completion-[0-9a-f]{64}$", id);
        Assert.Equal(id, AsiBackboneAuditCompletionPublisher.CreateLifecycleEventId("  completion-key  "));
        Assert.NotEqual(id, AsiBackboneAuditCompletionPublisher.CreateLifecycleEventId("completion-key-2"));
        _ = Assert.Throws<ArgumentException>(() => AsiBackboneAuditCompletionPublisher.CreateLifecycleEventId(" "));
    }

    /// <summary>
    /// Verifies the destination name and constructor validation.
    /// </summary>
    [Fact]
    public async Task Publisher_DestinationAndConstructorValidation()
    {
        await using TestApplicationServices app = await TestApplicationServices.CreateAsync(migrate: false);
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        IDbContextFactory<ApplicationDbContext> factory =
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        Assert.Equal(
            "asibackbone",
            new AsiBackboneAuditCompletionPublisher(
                factory,
                NullLoggerFactory.Instance,
                CreateNotificationPublisher(),
                GovernanceTelemetryProjection.Disabled).Destination);
        _ = Assert.Throws<ArgumentNullException>(() => new AsiBackboneAuditCompletionPublisher(
            null!,
            NullLoggerFactory.Instance,
            CreateNotificationPublisher(),
            GovernanceTelemetryProjection.Disabled));
        _ = Assert.Throws<ArgumentNullException>(() => new AsiBackboneAuditCompletionPublisher(
            factory,
            null!,
            CreateNotificationPublisher(),
            GovernanceTelemetryProjection.Disabled));
        _ = Assert.Throws<ArgumentNullException>(() => new AsiBackboneAuditCompletionPublisher(
            factory,
            NullLoggerFactory.Instance,
            null!,
            GovernanceTelemetryProjection.Disabled));
        _ = Assert.Throws<ArgumentNullException>(() => new AsiBackboneAuditCompletionPublisher(
            factory,
            NullLoggerFactory.Instance,
            CreateNotificationPublisher(),
            null!));
        _ = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await new AsiBackboneAuditCompletionPublisher(
                    factory,
                    NullLoggerFactory.Instance,
                    CreateNotificationPublisher(),
                    GovernanceTelemetryProjection.Disabled)
                .PublishAsync(null!, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies that the publisher is the registered destination for NCAT's audit-completion outbox.
    /// </summary>
    [Fact]
    public async Task Registration_UsesPublisherAsDefaultOutboxDestination()
    {
        await using TestApplicationServices app = await TestApplicationServices.CreateAsync(migrate: false);
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();

        IApplicationAuditCompletionPublisher publisher = Assert.Single(
            scope.ServiceProvider.GetServices<IApplicationAuditCompletionPublisher>());
        Assert.IsType<AsiBackboneAuditCompletionPublisher>(publisher);
        Assert.Equal(
            AsiBackboneAuditCompletionPublisher.DestinationName,
            scope.ServiceProvider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<ApplicationAuditCompletionOutboxOptions>>()
                .Value.DefaultDestination);
    }

    private static ApplicationAuditCompletionMessage CreateMessage(string decisionAuditRecordId)
    {
        return new ApplicationAuditCompletionMessage(
            SchemaVersion: "1.0",
            Destination: AsiBackboneAuditCompletionPublisher.DestinationName,
            IdempotencyKey: $"completion-{decisionAuditRecordId}",
            MutationBatchId: "batch-1",
            AuditRecordCount: 2,
            PersistenceOutcome: "Committed",
            ReceiptCompletedUtc: new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Unspecified),
            MutationManifestHash: _manifestHash,
            MutationManifestAlgorithm: "SHA-256",
            MutationManifestSchemaVersion: "1.0",
            OperationExecutionId: "execution-1",
            ExecutionAttemptId: "attempt-1",
            DecisionAuditRecordId: decisionAuditRecordId,
            CorrelationId: _correlationId,
            TraceId: _traceId);
    }

    private static async Task<AuditLedgerRecord> AppendDecisionAsync(TestApplicationServices app)
    {
        var record = AuditLedgerRecord.FromDecisionReceipt(DecisionReceipt.FromDecision(
            GovernanceActorContext.Human("user-123", "Test User"),
            "completion.test.operation",
            GovernanceDecision.Allow(correlationId: _correlationId, traceId: _traceId)));

        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        Assert.True((await new EfCoreAuditLedgerStore(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>())
            .AppendAsync(record, TestContext.Current.CancellationToken)).Succeeded);

        return record;
    }

    private static async Task<ApplicationAuditCompletionPublishResult> PublishAsync(
        TestApplicationServices app,
        ApplicationAuditCompletionMessage message)
    {
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IApplicationAuditCompletionPublisher>()
            .PublishAsync(message, TestContext.Current.CancellationToken);
    }

    private static async Task<DecisionReceiptLifecycleEvent?> FindLifecycleEventAsync(TestApplicationServices app, string eventId)
    {
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        return await new EfCoreDecisionReceiptLifecycleStore(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>())
            .FindByEventIdAsync(eventId, TestContext.Current.CancellationToken);
    }

    private static async Task<int> CountLifecycleEventsAsync(TestApplicationServices app)
    {
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .AsiBackboneDecisionReceiptLifecycleEvents
            .CountAsync(TestContext.Current.CancellationToken);
    }

    private static GovernanceEvidenceNotificationPublisher CreateNotificationPublisher()
    {
        return new GovernanceEvidenceNotificationPublisher(
            new NoopGovernanceEvidenceNotifier(),
            NullLogger<GovernanceEvidenceNotificationPublisher>.Instance);
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
