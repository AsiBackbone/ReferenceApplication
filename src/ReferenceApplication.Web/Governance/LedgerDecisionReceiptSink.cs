using AsiBackbone.Core.Audit;
using AsiBackbone.Core.Results;
using AsiBackbone.EntityFrameworkCore.Audit;
using Microsoft.EntityFrameworkCore;
using ReferenceApplication.Infrastructure.Data;

namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Persists endpoint governance decision receipts to the AsiBackbone audit ledger in the host-owned database.
/// </summary>
/// <remarks>
/// <para>
/// AsiBackbone's endpoint middleware writes a decision receipt to the registered <see cref="IDecisionReceiptSink" />
/// for every endpoint marked with <c>[EmitGovernanceAudit]</c>, before the endpoint executes. This sink converts the
/// receipt into an <see cref="AuditLedgerRecord" /> and appends it through AsiBackbone's
/// <see cref="EfCoreAuditLedgerStore" />.
/// </para>
/// <para>
/// Each append uses a short-lived <see cref="ApplicationDbContext" /> from the context factory, so persisting evidence
/// can never save, or be rolled back with, unrelated changes tracked by the request's own context. A failed append
/// throws <see cref="GovernanceEvidencePersistenceException" />, so the governed operation fails closed.
/// </para>
/// </remarks>
public sealed partial class LedgerDecisionReceiptSink : IDecisionReceiptSink
{
    /// <summary>
    /// The reason code used when the decision receipt cannot be appended to the audit ledger.
    /// </summary>
    public const string AppendFailedReasonCode = "referenceapplication.governance.evidence.append_failed";

    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly GovernanceDecisionEvidenceAccessor _evidenceAccessor;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<LedgerDecisionReceiptSink> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LedgerDecisionReceiptSink" /> class.
    /// </summary>
    /// <param name="dbContextFactory">The factory for short-lived application database contexts.</param>
    /// <param name="evidenceAccessor">The request-scoped evidence holder.</param>
    /// <param name="loggerFactory">The logger factory used for the AsiBackbone EF Core store.</param>
    public LedgerDecisionReceiptSink(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        GovernanceDecisionEvidenceAccessor evidenceAccessor,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(dbContextFactory);
        ArgumentNullException.ThrowIfNull(evidenceAccessor);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _dbContextFactory = dbContextFactory;
        _evidenceAccessor = evidenceAccessor;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<LedgerDecisionReceiptSink>();
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(IDecisionReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        cancellationToken.ThrowIfCancellationRequested();

        // One decision per request: reject a second receipt before persisting it, so no unlinked ledger row is written.
        if (_evidenceAccessor.Current is not null)
        {
            throw new InvalidOperationException(
                "Governance decision evidence was already recorded for this request.");
        }

        var record = AuditLedgerRecord.FromDecisionReceipt(receipt);
        OperationResult<AuditLedgerRecord> result;

        try
        {
            await using ApplicationDbContext dbContext = await _dbContextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            var store = new EfCoreAuditLedgerStore(dbContext, _loggerFactory.CreateLogger<EfCoreAuditLedgerStore>());

            result = await store.AppendAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogEvidencePersistenceFailed(_logger, receipt.OperationName, exception.GetType().Name, exception);
            throw new GovernanceEvidencePersistenceException(AppendFailedReasonCode, exception);
        }

        if (!result.Succeeded)
        {
            string reasonCode = result.Reasons.Count > 0 ? result.Reasons[0].Code : AppendFailedReasonCode;
            LogEvidenceAppendRejected(_logger, receipt.OperationName, reasonCode);
            throw new GovernanceEvidencePersistenceException(reasonCode);
        }

        _evidenceAccessor.Record(new GovernanceDecisionEvidence(
            DecisionAuditRecordId: record.RecordId,
            DecisionReceiptId: record.DecisionReceiptId,
            OperationName: record.OperationName,
            Outcome: record.Outcome,
            OperationExecutionId: Guid.NewGuid().ToString("N"),
            CorrelationId: record.CorrelationId,
            TraceId: record.TraceId));

        LogEvidencePersisted(_logger, record.OperationName, record.Outcome, record.RecordId);
    }

    [LoggerMessage(
        EventId = 26001,
        Level = LogLevel.Information,
        Message = "Persisted governance decision evidence for {OperationName} with outcome {Outcome} as audit ledger record {DecisionAuditRecordId}.")]
    private static partial void LogEvidencePersisted(
        ILogger logger,
        string operationName,
        string outcome,
        string decisionAuditRecordId);

    [LoggerMessage(
        EventId = 26002,
        Level = LogLevel.Error,
        Message = "The audit ledger rejected governance decision evidence for {OperationName} with reason {ReasonCode}.")]
    private static partial void LogEvidenceAppendRejected(ILogger logger, string operationName, string reasonCode);

    [LoggerMessage(
        EventId = 26003,
        Level = LogLevel.Error,
        Message = "Governance decision evidence for {OperationName} could not be persisted ({ExceptionType}).")]
    private static partial void LogEvidencePersistenceFailed(
        ILogger logger,
        string operationName,
        string exceptionType,
        Exception exception);
}
