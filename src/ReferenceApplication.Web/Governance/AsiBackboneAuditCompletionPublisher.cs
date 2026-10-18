using System.Security.Cryptography;
using System.Text;
using AsiBackbone.Core.Audit;
using AsiBackbone.Core.HostIntegration;
using AsiBackbone.EntityFrameworkCore.Audit;
using Microsoft.EntityFrameworkCore;
using ReferenceApplication.Infrastructure.Data;
using ReferenceApplication.Infrastructure.Data.Auditing;

namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Publishes NCAT audit-completion messages as AsiBackbone decision receipt lifecycle events.
/// </summary>
/// <remarks>
/// <para>
/// This is the host-owned form of AsiBackbone's optional NCAT audit-completion adapter. NCAT's completion outbox
/// dispatches each committed mutation batch to the publisher registered for its destination. The publisher resolves
/// the AsiBackbone audit ledger record named by the message's decision audit record identifier and appends a
/// <see cref="DecisionReceiptLifecycleStage.GatewayExecutionCompleted" /> lifecycle event that carries the mutation
/// batch, record count, manifest hash, and persistence outcome. The decision and the mutation that it permitted are
/// then linked in both directions.
/// </para>
/// <para>
/// Delivery is at least once. The lifecycle event identifier is derived from NCAT's idempotency key, so a redelivered
/// message is recognized as a duplicate, and a different event under the same key is reported as a terminal conflict.
/// A message whose decision record is not yet available is deferred rather than guessed.
/// </para>
/// </remarks>
public sealed class AsiBackboneAuditCompletionPublisher : IApplicationAuditCompletionPublisher
{
    /// <summary>
    /// The NCAT audit-completion destination served by this publisher.
    /// </summary>
    public const string DestinationName = "asibackbone";

    /// <summary>
    /// The persistence provider label written to the lifecycle event.
    /// </summary>
    public const string PersistenceProviderName = "NCAT";

    /// <summary>
    /// The lifecycle metadata key that carries NCAT's completion idempotency key.
    /// </summary>
    public const string CompletionEntryIdMetadataKey = "ncatCompletionEntryId";

    /// <summary>
    /// The lifecycle metadata key that identifies the adapter that wrote the event.
    /// </summary>
    public const string CompletionAdapterMetadataKey = "completionAdapter";

    /// <summary>
    /// The prefix of deterministic lifecycle event identifiers.
    /// </summary>
    public const string LifecycleEventIdPrefix = "ncat-completion-";

    private static readonly string[] _equivalenceMetadataKeys =
    [
        CompletionEntryIdMetadataKey,
        HostAccountabilityMetadataKeys.OperationExecutionId,
        HostAccountabilityMetadataKeys.ExecutionAttemptId,
        HostAccountabilityMetadataKeys.DecisionAuditRecordId,
        HostAccountabilityMetadataKeys.PersistenceOutcome,
        HostAccountabilityMetadataKeys.MutationBatchId,
        HostAccountabilityMetadataKeys.MutationRecordCount,
        HostAccountabilityMetadataKeys.MutationManifestHash,
        HostAccountabilityMetadataKeys.MutationManifestAlgorithm
    ];

    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="AsiBackboneAuditCompletionPublisher" /> class.
    /// </summary>
    /// <param name="dbContextFactory">The factory for short-lived application database contexts.</param>
    /// <param name="loggerFactory">The logger factory used for the AsiBackbone EF Core stores.</param>
    public AsiBackboneAuditCompletionPublisher(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(dbContextFactory);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _dbContextFactory = dbContextFactory;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public string Destination => DestinationName;

    /// <summary>
    /// Creates the deterministic lifecycle event identifier for an NCAT completion idempotency key.
    /// </summary>
    /// <param name="idempotencyKey">The NCAT completion idempotency key.</param>
    /// <returns>The lifecycle event identifier.</returns>
    public static string CreateLifecycleEventId(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey.Trim()));
        return string.Concat(LifecycleEventIdPrefix, Convert.ToHexStringLower(digest));
    }

    /// <inheritdoc />
    public async ValueTask<ApplicationAuditCompletionPublishResult> PublishAsync(
        ApplicationAuditCompletionMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        string? validationFailure = Validate(message);
        if (validationFailure is not null)
        {
            return ApplicationAuditCompletionPublishResult.TerminalFailure(validationFailure);
        }

        if (!TryMapOutcome(message.PersistenceOutcome, out GovernedOperationPersistenceOutcome persistenceOutcome))
        {
            return ApplicationAuditCompletionPublishResult.TerminalFailure("unsupported-persistence-outcome");
        }

        await using ApplicationDbContext dbContext = await _dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var ledgerStore = new EfCoreAuditLedgerStore(dbContext, _loggerFactory.CreateLogger<EfCoreAuditLedgerStore>());
        var lifecycleStore = new EfCoreDecisionReceiptLifecycleStore(dbContext);

        AuditLedgerRecord? decisionRecord = await ledgerStore
            .FindByRecordIdAsync(message.DecisionAuditRecordId!.Trim(), cancellationToken)
            .ConfigureAwait(false);

        if (decisionRecord is null)
        {
            return ApplicationAuditCompletionPublishResult.Defer("decision-receipt-not-available");
        }

        string? correlationFailure = ValidateCorrelation(message, decisionRecord);
        if (correlationFailure is not null)
        {
            return ApplicationAuditCompletionPublishResult.TerminalFailure(correlationFailure);
        }

        GovernedOperationExecutionReceipt executionReceipt;
        try
        {
            executionReceipt = GovernedOperationExecutionReceipt.Create(
                operationExecutionId: message.OperationExecutionId!.Trim(),
                persistenceOutcome: persistenceOutcome,
                executionAttemptId: NormalizeOptional(message.ExecutionAttemptId),
                mutationBatchId: NormalizeOptional(message.MutationBatchId),
                mutationRecordCount: message.AuditRecordCount,
                mutationManifestHash: NormalizeOptional(message.MutationManifestHash),
                mutationManifestAlgorithm: NormalizeOptional(message.MutationManifestAlgorithm),
                completedUtc: ToUtcOffset(message.ReceiptCompletedUtc),
                persistenceProvider: PersistenceProviderName,
                decisionAuditRecordId: decisionRecord.RecordId);
        }
        catch (ArgumentException)
        {
            return ApplicationAuditCompletionPublishResult.TerminalFailure("invalid-execution-receipt");
        }

        string lifecycleEventId = CreateLifecycleEventId(message.IdempotencyKey);
        DecisionReceiptLifecycleEvent lifecycleEvent = HostAccountabilityLifecycleEvent.FromExecutionReceipt(
            decisionRecord,
            executionReceipt,
            eventId: lifecycleEventId,
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CompletionEntryIdMetadataKey] = message.IdempotencyKey.Trim(),
                [CompletionAdapterMetadataKey] = PersistenceProviderName
            });

        DecisionReceiptLifecycleEvent? existing = await lifecycleStore
            .FindByEventIdAsync(lifecycleEventId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return IsEquivalent(existing, lifecycleEvent)
                ? ApplicationAuditCompletionPublishResult.Success()
                : ApplicationAuditCompletionPublishResult.TerminalFailure("idempotency-conflict");
        }

        try
        {
            _ = await lifecycleStore.AppendAsync(lifecycleEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is DbUpdateException or InvalidOperationException)
        {
            return ApplicationAuditCompletionPublishResult.Retry(
                "lifecycle-persistence-failed",
                exception.GetType().Name);
        }

        return ApplicationAuditCompletionPublishResult.Success();
    }

    private static string? Validate(ApplicationAuditCompletionMessage message)
    {
        return string.IsNullOrWhiteSpace(message.IdempotencyKey)
            ? "completion-entry-id-required"
            : string.IsNullOrWhiteSpace(message.OperationExecutionId)
            ? "operation-execution-id-required"
            : string.IsNullOrWhiteSpace(message.DecisionAuditRecordId)
            ? "decision-audit-record-id-required"
            : string.IsNullOrWhiteSpace(message.PersistenceOutcome)
            ? "persistence-outcome-required"
            : message.AuditRecordCount < 0
            ? "audit-record-count-invalid"
            : null;
    }

    private static string? ValidateCorrelation(ApplicationAuditCompletionMessage message, AuditLedgerRecord decision)
    {
        string? correlationId = NormalizeOptional(message.CorrelationId);
        if (correlationId is not null &&
            !string.Equals(correlationId, decision.CorrelationId, StringComparison.Ordinal))
        {
            return "correlation-id-mismatch";
        }

        string? traceId = NormalizeOptional(message.TraceId);
        return traceId is not null && !string.Equals(traceId, decision.TraceId, StringComparison.Ordinal)
            ? "trace-id-mismatch"
            : null;
    }

    private static bool TryMapOutcome(string sourceOutcome, out GovernedOperationPersistenceOutcome persistenceOutcome)
    {
        string normalized = string.Concat(sourceOutcome
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant));

        (bool mapped, persistenceOutcome) = normalized switch
        {
            "committed" => (true, GovernedOperationPersistenceOutcome.Committed),
            "failed" => (true, GovernedOperationPersistenceOutcome.Failed),
            "rolledback" => (true, GovernedOperationPersistenceOutcome.RolledBack),
            "completedwithoutmutation" or "nomutation" => (true, GovernedOperationPersistenceOutcome.CompletedWithoutMutation),
            _ => (false, default)
        };

        return mapped;
    }

    private static DateTimeOffset ToUtcOffset(DateTime value)
    {
        DateTime utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private static bool IsEquivalent(DecisionReceiptLifecycleEvent existing, DecisionReceiptLifecycleEvent candidate)
    {
        return existing.Stage == candidate.Stage &&
            string.Equals(existing.CorrelationId, candidate.CorrelationId, StringComparison.Ordinal) &&
            string.Equals(existing.DecisionReceiptId, candidate.DecisionReceiptId, StringComparison.Ordinal) &&
            string.Equals(existing.TraceId, candidate.TraceId, StringComparison.Ordinal) &&
            string.Equals(existing.OperationName, candidate.OperationName, StringComparison.Ordinal) &&
            string.Equals(existing.Outcome, candidate.Outcome, StringComparison.Ordinal) &&
            _equivalenceMetadataKeys.All(key => string.Equals(
                GetMetadata(existing.Metadata, key),
                GetMetadata(candidate.Metadata, key),
                StringComparison.Ordinal));
    }

    private static string? GetMetadata(IReadOnlyDictionary<string, string> metadata, string key)
    {
        return metadata.TryGetValue(key, out string? value) ? value : null;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
