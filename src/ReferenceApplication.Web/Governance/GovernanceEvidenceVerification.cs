using AsiBackbone.Core.Audit;
using AsiBackbone.Core.Signing;
using AsiBackbone.EntityFrameworkCore.Audit;
using Microsoft.EntityFrameworkCore;
using ReferenceApplication.Infrastructure.Data;

namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Verifies the canonical hashes stored with governance decision evidence.
/// </summary>
public interface IGovernanceEvidenceVerifier
{
    /// <summary>
    /// Verifies every decision ledger record associated with a correlation identifier.
    /// </summary>
    /// <param name="correlationId">The exact request correlation identifier.</param>
    /// <param name="cancellationToken">A token that cancels the verification.</param>
    /// <returns>The verification results, or <see langword="null" /> when no decision evidence exists.</returns>
    ValueTask<GovernanceEvidenceVerificationResponse?> VerifyAsync(
        string correlationId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stable verification status values returned by the evidence API.
/// </summary>
public static class GovernanceEvidenceVerificationStatuses
{
    /// <summary>The stored hash matches the current canonical record.</summary>
    public const string Verified = "verified";

    /// <summary>The stored hash does not match the current canonical record.</summary>
    public const string Mismatch = "mismatch";

    /// <summary>The record predates hashing and has no stored hash to verify.</summary>
    public const string NotHashed = "not-hashed";
}

/// <summary>
/// Creates and verifies canonical audit-ledger record hashes.
/// </summary>
internal static class AuditLedgerRecordHashing
{
    public static AuditLedgerRecord CreateHashed(IDecisionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        var unhashed = AuditLedgerRecord.FromDecisionReceipt(receipt);
        CanonicalPayloadHash hash = ComputeHash(unhashed);

        return AuditLedgerRecord.FromDecisionReceipt(
            receipt,
            recordId: unhashed.RecordId,
            recordedUtc: unhashed.RecordedUtc,
            recordHash: hash.HashValue);
    }

    public static CanonicalPayloadHash ComputeHash(AuditLedgerRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return CanonicalPayloadHasher.ComputeHash(CanonicalPayloadBuilder.ForAuditLedgerRecord(record));
    }
}

/// <summary>
/// Verifies persisted evidence through a short-lived host-owned database context.
/// </summary>
internal sealed class GovernanceEvidenceVerifier(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    ILoggerFactory loggerFactory) : IGovernanceEvidenceVerifier
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory =
        dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    private readonly ILoggerFactory _loggerFactory =
        loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

    /// <inheritdoc />
    public async ValueTask<GovernanceEvidenceVerificationResponse?> VerifyAsync(
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        cancellationToken.ThrowIfCancellationRequested();

        string normalizedCorrelationId = correlationId.Trim();
        await using ApplicationDbContext dbContext = await _dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        string[] recordIds = await dbContext.AsiBackboneAuditLedgerRecords
            .AsNoTracking()
            .Where(record => record.CorrelationId == normalizedCorrelationId)
            .OrderBy(record => record.RecordId)
            .Select(record => record.RecordId)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        if (recordIds.Length == 0)
        {
            return null;
        }

        var store = new EfCoreAuditLedgerStore(
            dbContext,
            _loggerFactory.CreateLogger<EfCoreAuditLedgerStore>());
        var results = new List<GovernanceEvidenceRecordVerification>(recordIds.Length);

        foreach (string recordId in recordIds)
        {
            AuditLedgerRecord? record = await store
                .FindByRecordIdAsync(recordId, cancellationToken)
                .ConfigureAwait(false);

            if (record is null)
            {
                continue;
            }

            results.Add(VerifyRecord(record));
        }

        return new GovernanceEvidenceVerificationResponse(normalizedCorrelationId, results);
    }

    /// <summary>
    /// Recomputes one ledger record's canonical hash and compares it with the hash stored on the record.
    /// </summary>
    /// <param name="record">The record to verify, as read from the ledger or as an in-memory copy.</param>
    /// <returns>The verification result.</returns>
    /// <remarks>
    /// The Development failure scenarios verify an in-memory copy of a persisted record through this method, so they
    /// show exactly what this verifier reports for a changed record.
    /// </remarks>
    internal static GovernanceEvidenceRecordVerification VerifyRecord(AuditLedgerRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (string.IsNullOrWhiteSpace(record.RecordHash))
        {
            return new GovernanceEvidenceRecordVerification(
                record.RecordId,
                GovernanceEvidenceVerificationStatuses.NotHashed,
                StoredHash: null,
                ComputedHash: null,
                HashAlgorithm: null);
        }

        CanonicalPayloadHash computed = AuditLedgerRecordHashing.ComputeHash(record);
        string status = string.Equals(record.RecordHash, computed.HashValue, StringComparison.Ordinal)
            ? GovernanceEvidenceVerificationStatuses.Verified
            : GovernanceEvidenceVerificationStatuses.Mismatch;
        return new GovernanceEvidenceRecordVerification(
            record.RecordId,
            status,
            record.RecordHash,
            computed.HashValue,
            computed.HashAlgorithm);
    }
}

/// <summary>
/// Represents verification results for the decision evidence associated with a governed request.
/// </summary>
/// <param name="CorrelationId">The exact request correlation identifier.</param>
/// <param name="Records">The decision ledger record verification results.</param>
public sealed record GovernanceEvidenceVerificationResponse(
    string CorrelationId,
    IReadOnlyList<GovernanceEvidenceRecordVerification> Records);

/// <summary>
/// Represents the canonical-hash verification result for one decision ledger record.
/// </summary>
/// <param name="RecordId">The ledger record identifier.</param>
/// <param name="Status">One of the stable <see cref="GovernanceEvidenceVerificationStatuses" /> values.</param>
/// <param name="StoredHash">The hash stored with the record, when present.</param>
/// <param name="ComputedHash">The hash recomputed from the current canonical record, when verification was possible.</param>
/// <param name="HashAlgorithm">The algorithm used to recompute the hash.</param>
public sealed record GovernanceEvidenceRecordVerification(
    string RecordId,
    string Status,
    string? StoredHash,
    string? ComputedHash,
    string? HashAlgorithm);
