using AsiBackbone.Core.Actors;
using AsiBackbone.Core.Audit;
using AsiBackbone.Core.Decisions;
using AsiBackbone.EntityFrameworkCore.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceApplication.Infrastructure.Data;
using ReferenceApplication.Web.Governance;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies <see cref="LedgerDecisionReceiptSink" /> and <see cref="GovernanceDecisionEvidenceAccessor" />, which persist
/// endpoint governance decision receipts and expose the persisted evidence to the request (walkthrough Step 6).
/// </summary>
public sealed class LedgerDecisionReceiptSinkTests
{
    /// <summary>
    /// Verifies that a receipt is appended to the audit ledger with its decision fields intact and that the request
    /// evidence points at the persisted ledger record.
    /// </summary>
    [Fact]
    public async Task WriteAsync_PersistsLedgerRecord_AndRecordsEvidence()
    {
        await using TestApplicationServices app = await TestApplicationServices.CreateAsync();
        DecisionReceipt receipt = CreateReceipt(GovernanceDecision.Deny(
            "test.denied",
            "Denied by a test.",
            correlationId: "correlation-1",
            traceId: "0af7651916cd43dd8448eb211c80319c",
            policyVersion: "1.0.0",
            policyHash: "sha256:test"));

        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        IDecisionReceiptSink sink = scope.ServiceProvider.GetRequiredService<IDecisionReceiptSink>();
        await sink.WriteAsync(receipt, TestContext.Current.CancellationToken);

        GovernanceDecisionEvidence evidence = Assert.IsType<GovernanceDecisionEvidence>(
            scope.ServiceProvider.GetRequiredService<IGovernanceDecisionEvidenceAccessor>().Current);
        AuditLedgerRecord? stored = await FindLedgerRecordAsync(app, evidence.DecisionAuditRecordId);

        Assert.NotNull(stored);
        Assert.Equal(receipt.EventId, stored.EventId);
        Assert.Equal("user-123", stored.ActorId);
        Assert.Equal(GovernanceActorType.Human, stored.ActorType);
        Assert.Equal("sink.test.operation", stored.OperationName);
        Assert.Equal(GovernanceDecisionOutcome.Denied.ToString(), stored.Outcome);
        Assert.Equal(["test.denied"], stored.ReasonCodes);
        Assert.Equal("correlation-1", stored.CorrelationId);
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", stored.TraceId);
        Assert.Equal("1.0.0", stored.PolicyVersion);
        Assert.Equal("sha256:test", stored.PolicyHash);
        Assert.Equal("value", stored.Metadata["endpoint.test"]);

        Assert.Equal(stored.OperationName, evidence.OperationName);
        Assert.Equal(stored.Outcome, evidence.Outcome);
        Assert.Equal(stored.CorrelationId, evidence.CorrelationId);
        Assert.Equal(stored.TraceId, evidence.TraceId);
        Assert.Equal(stored.DecisionReceiptId, evidence.DecisionReceiptId);
        Assert.Matches("^[0-9a-f]{32}$", evidence.OperationExecutionId);
    }

    /// <summary>
    /// Verifies that each request scope gets its own evidence and execution identifier.
    /// </summary>
    [Fact]
    public async Task WriteAsync_SeparateRequests_GetDistinctEvidence()
    {
        await using TestApplicationServices app = await TestApplicationServices.CreateAsync();

        GovernanceDecisionEvidence first = await WriteInNewScopeAsync(app);
        GovernanceDecisionEvidence second = await WriteInNewScopeAsync(app);

        Assert.NotEqual(first.DecisionAuditRecordId, second.DecisionAuditRecordId);
        Assert.NotEqual(first.OperationExecutionId, second.OperationExecutionId);
    }

    /// <summary>
    /// Verifies that a second receipt in the same request is rejected before anything is persisted, so a request can
    /// never relink its mutations to a different decision or leave an unlinked ledger row.
    /// </summary>
    [Fact]
    public async Task WriteAsync_SecondReceiptInSameRequest_IsRejectedWithoutPersisting()
    {
        await using TestApplicationServices app = await TestApplicationServices.CreateAsync();
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        IDecisionReceiptSink sink = scope.ServiceProvider.GetRequiredService<IDecisionReceiptSink>();

        await sink.WriteAsync(CreateReceipt(GovernanceDecision.Allow()), TestContext.Current.CancellationToken);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sink.WriteAsync(CreateReceipt(GovernanceDecision.Allow()), TestContext.Current.CancellationToken));

        Assert.Equal(1, await CountLedgerRecordsAsync(app));
    }

    /// <summary>
    /// Verifies that the sink fails closed with a sanitized exception, and records no evidence, when the database
    /// cannot store the ledger record.
    /// </summary>
    [Fact]
    public async Task WriteAsync_DatabaseCannotStoreEvidence_FailsClosed()
    {
        await using TestApplicationServices app = await TestApplicationServices.CreateAsync(migrate: false);
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        IDecisionReceiptSink sink = scope.ServiceProvider.GetRequiredService<IDecisionReceiptSink>();

        GovernanceEvidencePersistenceException exception = await Assert.ThrowsAsync<GovernanceEvidencePersistenceException>(
            async () => await sink.WriteAsync(CreateReceipt(GovernanceDecision.Allow()), TestContext.Current.CancellationToken));

        // AsiBackbone's EF Core store reports the failed append with its sanitized reason code.
        Assert.Equal("asi_backbone.audit_ledger.append_failed", exception.ReasonCode);
        Assert.Equal("Governance decision evidence could not be persisted.", exception.Message);
        Assert.Null(scope.ServiceProvider.GetRequiredService<IGovernanceDecisionEvidenceAccessor>().Current);
    }

    /// <summary>
    /// Verifies that the sink also fails closed when persistence throws outside the store's own error handling, such
    /// as when the database cannot be reached, and keeps the original exception for host diagnostics.
    /// </summary>
    [Fact]
    public async Task WriteAsync_PersistenceThrows_FailsClosedWithSanitizedException()
    {
        var accessor = new GovernanceDecisionEvidenceAccessor();
        var sink = new LedgerDecisionReceiptSink(
            new ThrowingDbContextFactory(),
            accessor,
            NullLoggerFactory.Instance,
            CreateNotificationPublisher());

        GovernanceEvidencePersistenceException exception = await Assert.ThrowsAsync<GovernanceEvidencePersistenceException>(
            async () => await sink.WriteAsync(CreateReceipt(GovernanceDecision.Allow()), TestContext.Current.CancellationToken));

        Assert.Equal(LedgerDecisionReceiptSink.AppendFailedReasonCode, exception.ReasonCode);
        Assert.Equal("Governance decision evidence could not be persisted.", exception.Message);
        _ = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Null(accessor.Current);
    }

    /// <summary>
    /// Verifies that cancellation is not converted into a persistence failure.
    /// </summary>
    [Fact]
    public async Task WriteAsync_Cancelled_PropagatesCancellation()
    {
        var sink = new LedgerDecisionReceiptSink(
            new ThrowingDbContextFactory(),
            new GovernanceDecisionEvidenceAccessor(),
            NullLoggerFactory.Instance,
            CreateNotificationPublisher());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        _ = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await sink.WriteAsync(CreateReceipt(GovernanceDecision.Allow()), cancelled.Token));
    }

    /// <summary>
    /// Verifies argument validation.
    /// </summary>
    [Fact]
    public async Task WriteAsync_NullReceipt_Throws()
    {
        await using TestApplicationServices app = await TestApplicationServices.CreateAsync(migrate: false);
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        IDecisionReceiptSink sink = scope.ServiceProvider.GetRequiredService<IDecisionReceiptSink>();

        _ = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await sink.WriteAsync(null!, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies constructor argument validation.
    /// </summary>
    [Fact]
    public async Task Constructor_NullArguments_Throw()
    {
        await using TestApplicationServices app = await TestApplicationServices.CreateAsync(migrate: false);
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        IDbContextFactory<ApplicationDbContext> factory =
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        GovernanceDecisionEvidenceAccessor accessor = new();

        _ = Assert.Throws<ArgumentNullException>(() =>
            new LedgerDecisionReceiptSink(null!, accessor, NullLoggerFactory.Instance, CreateNotificationPublisher()));
        _ = Assert.Throws<ArgumentNullException>(() =>
            new LedgerDecisionReceiptSink(factory, null!, NullLoggerFactory.Instance, CreateNotificationPublisher()));
        _ = Assert.Throws<ArgumentNullException>(() =>
            new LedgerDecisionReceiptSink(factory, accessor, null!, CreateNotificationPublisher()));
        _ = Assert.Throws<ArgumentNullException>(() =>
            new LedgerDecisionReceiptSink(factory, accessor, NullLoggerFactory.Instance, null!));
    }

    /// <summary>
    /// Verifies that the evidence holder starts empty, accepts one record, and rejects a second.
    /// </summary>
    [Fact]
    public void EvidenceAccessor_RecordsOnce()
    {
        GovernanceDecisionEvidenceAccessor accessor = new();
        var evidence = new GovernanceDecisionEvidence("record-1", null, "operation", "Allowed", "execution-1", null, null);

        Assert.Null(accessor.Current);
        accessor.Record(evidence);

        Assert.Same(evidence, accessor.Current);
        _ = Assert.Throws<InvalidOperationException>(() => accessor.Record(evidence));
        _ = Assert.Throws<ArgumentNullException>(() => new GovernanceDecisionEvidenceAccessor().Record(null!));
    }

    /// <summary>
    /// Verifies that the persistence exception always carries a reason code.
    /// </summary>
    [Fact]
    public void PersistenceException_DefaultsReasonCode()
    {
        Assert.Equal(LedgerDecisionReceiptSink.AppendFailedReasonCode, new GovernanceEvidencePersistenceException().ReasonCode);
        Assert.Equal(LedgerDecisionReceiptSink.AppendFailedReasonCode, new GovernanceEvidencePersistenceException(" ").ReasonCode);
        Assert.Equal("custom", new GovernanceEvidencePersistenceException("custom").ReasonCode);

        var inner = new InvalidOperationException("inner");
        var wrapped = new GovernanceEvidencePersistenceException("custom", inner);
        Assert.Same(inner, wrapped.InnerException);
        Assert.Equal("custom", wrapped.ReasonCode);
    }

    private static async Task<GovernanceDecisionEvidence> WriteInNewScopeAsync(TestApplicationServices app)
    {
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDecisionReceiptSink>()
            .WriteAsync(CreateReceipt(GovernanceDecision.Allow()), TestContext.Current.CancellationToken);

        return scope.ServiceProvider.GetRequiredService<IGovernanceDecisionEvidenceAccessor>().Current!;
    }

    private static DecisionReceipt CreateReceipt(GovernanceDecision decision)
    {
        return DecisionReceipt.FromDecision(
            GovernanceActorContext.Human("user-123", "Test User"),
            "sink.test.operation",
            decision,
            metadata: new Dictionary<string, string>(StringComparer.Ordinal) { ["endpoint.test"] = "value" });
    }

    private static async Task<AuditLedgerRecord?> FindLedgerRecordAsync(TestApplicationServices app, string recordId)
    {
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        return await new EfCoreAuditLedgerStore(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>())
            .FindByRecordIdAsync(recordId, TestContext.Current.CancellationToken);
    }

    private sealed class ThrowingDbContextFactory : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext()
        {
            throw new InvalidOperationException("The database cannot be reached.");
        }
    }

    private static async Task<int> CountLedgerRecordsAsync(TestApplicationServices app)
    {
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .AsiBackboneAuditLedgerRecords
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
