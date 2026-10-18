using System.Text.Json;
using AsiBackbone.Core.HostIntegration;
using Microsoft.EntityFrameworkCore;
using ReferenceApplication.Infrastructure.Data;
using ReferenceApplication.Infrastructure.Data.Entities;

namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Reads the minimized evidence timeline for a governed request.
/// </summary>
public interface IGovernanceEvidenceQuery
{
    /// <summary>
    /// Finds the evidence associated with a request correlation identifier.
    /// </summary>
    /// <param name="correlationId">The exact request correlation identifier.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The ordered timeline, or <see langword="null" /> when no decision evidence exists.</returns>
    ValueTask<GovernanceEvidenceTimelineResponse?> FindAsync(
        string correlationId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Queries governance and application audit evidence through a short-lived database context.
/// </summary>
internal sealed class GovernanceEvidenceQuery(IDbContextFactory<ApplicationDbContext> dbContextFactory)
    : IGovernanceEvidenceQuery
{
    private const string _applicationRecordKind = "application-record";
    private const string _auditCompletionKind = "audit-completion";
    private const string _decisionKind = "decision";
    private const string _lifecycleEventKind = "lifecycle-event";
    private const string _mutationAuditKind = "mutation-audit";

    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory =
        dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));

    /// <inheritdoc />
    public async ValueTask<GovernanceEvidenceTimelineResponse?> FindAsync(
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        cancellationToken.ThrowIfCancellationRequested();

        string normalizedCorrelationId = correlationId.Trim();
        await using ApplicationDbContext dbContext = await _dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        List<AsiBackbone.EntityFrameworkCore.Persistence.AuditLedgerRecordEntity> decisions = await dbContext
            .AsiBackboneAuditLedgerRecords
            .AsNoTracking()
            .Where(record => record.CorrelationId == normalizedCorrelationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (decisions.Count == 0)
        {
            return null;
        }

        string[] decisionRecordIds = [.. decisions.Select(record => record.RecordId)];
        List<AuditRecord> mutationAudits = await dbContext.AuditRecords
            .AsNoTracking()
            .Where(record => record.CorrelationId == normalizedCorrelationId ||
                (record.DecisionAuditRecordId != null && decisionRecordIds.Contains(record.DecisionAuditRecordId)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Guid[] applicationRecordIds = [.. mutationAudits
            .Select(record => TryReadEntityId(record.KeyValues))
            .OfType<Guid>()
            .Distinct()];
        List<GovernedOperationRecord> applicationRecords = applicationRecordIds.Length == 0
            ? []
            : await dbContext.GovernedOperationRecords
                .AsNoTracking()
                .Where(record => applicationRecordIds.Contains(record.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        List<ApplicationAuditCompletionOutboxEntry> completions = await dbContext
            .ApplicationAuditCompletionOutboxEntries
            .AsNoTracking()
            .Where(entry => entry.CorrelationId == normalizedCorrelationId ||
                (entry.DecisionAuditRecordId != null && decisionRecordIds.Contains(entry.DecisionAuditRecordId)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<AsiBackbone.EntityFrameworkCore.Persistence.DecisionReceiptLifecycleEventEntity> lifecycleEvents =
            await dbContext.AsiBackboneDecisionReceiptLifecycleEvents
                .AsNoTracking()
                .Where(entry => entry.CorrelationId == normalizedCorrelationId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        var entries = new List<GovernanceEvidenceTimelineEntry>(
            decisions.Count + mutationAudits.Count + applicationRecords.Count + completions.Count + lifecycleEvents.Count);

        entries.AddRange(decisions.Select(decision => new GovernanceEvidenceTimelineEntry(
            Kind: _decisionKind,
            OccurredUtc: decision.OccurredUtc,
            EvidenceId: decision.RecordId,
            DecisionAuditRecordId: decision.RecordId,
            DecisionReceiptId: decision.DecisionReceiptId,
            OperationName: decision.OperationName,
            Outcome: decision.Outcome,
            Status: null,
            ReasonCodes: ReadReasonCodes(decision.ReasonCodesJson),
            PolicyVersion: decision.PolicyVersion,
            PolicyHash: decision.PolicyHash,
            ActorId: decision.ActorId,
            ActorType: decision.ActorType.ToString(),
            OperationExecutionId: null,
            MutationBatchId: null,
            RecordCount: null,
            EvidenceHash: decision.RecordHash,
            EvidenceHashAlgorithm: null)));

        entries.AddRange(applicationRecords.Select(record => new GovernanceEvidenceTimelineEntry(
            Kind: _applicationRecordKind,
            OccurredUtc: record.CreatedUtc,
            EvidenceId: record.Id.ToString("D"),
            DecisionAuditRecordId: FindDecisionRecordId(mutationAudits, record.Id),
            DecisionReceiptId: null,
            OperationName: record.OperationName,
            Outcome: null,
            Status: "Persisted",
            ReasonCodes: [],
            PolicyVersion: null,
            PolicyHash: null,
            ActorId: null,
            ActorType: null,
            OperationExecutionId: FindOperationExecutionId(mutationAudits, record.Id),
            MutationBatchId: FindMutationBatchId(mutationAudits, record.Id),
            RecordCount: null,
            EvidenceHash: null,
            EvidenceHashAlgorithm: null)));

        entries.AddRange(mutationAudits.Select(record => new GovernanceEvidenceTimelineEntry(
            Kind: _mutationAuditKind,
            OccurredUtc: ToUtcOffset(record.ModifiedOnUtc),
            EvidenceId: record.Id.ToString("D"),
            DecisionAuditRecordId: record.DecisionAuditRecordId,
            DecisionReceiptId: null,
            OperationName: null,
            Outcome: record.State,
            Status: "Persisted",
            ReasonCodes: [],
            PolicyVersion: null,
            PolicyHash: null,
            ActorId: record.ActorId,
            ActorType: record.ActorType,
            OperationExecutionId: record.OperationExecutionId,
            MutationBatchId: record.MutationBatchId,
            RecordCount: 1,
            EvidenceHash: null,
            EvidenceHashAlgorithm: null)));

        entries.AddRange(completions.Select(entry => new GovernanceEvidenceTimelineEntry(
            Kind: _auditCompletionKind,
            OccurredUtc: ToUtcOffset(entry.DeliveredUtc ?? entry.CreatedUtc),
            EvidenceId: entry.Id.ToString("D"),
            DecisionAuditRecordId: entry.DecisionAuditRecordId,
            DecisionReceiptId: null,
            OperationName: null,
            Outcome: entry.PersistenceOutcome,
            Status: entry.Status,
            ReasonCodes: string.IsNullOrWhiteSpace(entry.LastErrorCode) ? [] : [entry.LastErrorCode],
            PolicyVersion: null,
            PolicyHash: null,
            ActorId: null,
            ActorType: null,
            OperationExecutionId: entry.OperationExecutionId,
            MutationBatchId: entry.MutationBatchId,
            RecordCount: entry.AuditRecordCount,
            EvidenceHash: entry.MutationManifestHash,
            EvidenceHashAlgorithm: entry.MutationManifestAlgorithm)));

        entries.AddRange(lifecycleEvents.Select(entry =>
        {
            Dictionary<string, string> metadata = ReadMetadata(entry.MetadataJson);
            return new GovernanceEvidenceTimelineEntry(
                Kind: _lifecycleEventKind,
                OccurredUtc: entry.OccurredUtc,
                EvidenceId: entry.EventId,
                DecisionAuditRecordId: GetMetadata(metadata, HostAccountabilityMetadataKeys.DecisionAuditRecordId),
                DecisionReceiptId: entry.DecisionReceiptId,
                OperationName: entry.OperationName,
                Outcome: entry.Outcome,
                Status: entry.Stage.ToString(),
                ReasonCodes: [],
                PolicyVersion: null,
                PolicyHash: null,
                ActorId: null,
                ActorType: null,
                OperationExecutionId: GetMetadata(metadata, HostAccountabilityMetadataKeys.OperationExecutionId),
                MutationBatchId: GetMetadata(metadata, HostAccountabilityMetadataKeys.MutationBatchId),
                RecordCount: TryReadInt32(metadata, HostAccountabilityMetadataKeys.MutationRecordCount),
                EvidenceHash: GetMetadata(metadata, HostAccountabilityMetadataKeys.MutationManifestHash),
                EvidenceHashAlgorithm: GetMetadata(metadata, HostAccountabilityMetadataKeys.MutationManifestAlgorithm));
        }));

        return new GovernanceEvidenceTimelineResponse(
            normalizedCorrelationId,
            [.. entries.OrderBy(entry => entry.OccurredUtc)
                .ThenBy(entry => entry.Kind, StringComparer.Ordinal)
                .ThenBy(entry => entry.EvidenceId, StringComparer.Ordinal)]);
    }

    private static Guid? TryReadEntityId(string keyValues)
    {
        try
        {
            using var document = JsonDocument.Parse(keyValues);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, nameof(GovernedOperationRecord.Id), StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(property.Value.GetString(), out Guid id))
                {
                    return id;
                }
            }
        }
        catch (JsonException)
        {
            // Malformed legacy key data cannot safely identify an application record, so omit that summary.
        }

        return null;
    }

    private static string? FindDecisionRecordId(IEnumerable<AuditRecord> records, Guid applicationRecordId)
    {
        return FindMutationAudit(records, applicationRecordId)?.DecisionAuditRecordId;
    }

    private static string? FindOperationExecutionId(IEnumerable<AuditRecord> records, Guid applicationRecordId)
    {
        return FindMutationAudit(records, applicationRecordId)?.OperationExecutionId;
    }

    private static string? FindMutationBatchId(IEnumerable<AuditRecord> records, Guid applicationRecordId)
    {
        return FindMutationAudit(records, applicationRecordId)?.MutationBatchId;
    }

    private static AuditRecord? FindMutationAudit(IEnumerable<AuditRecord> records, Guid applicationRecordId)
    {
        AuditRecord? matchingRecord = records
            .Where(record => TryReadEntityId(record.KeyValues) == applicationRecordId)
            .OrderBy(record => record.ModifiedOnUtc)
            .ThenBy(record => record.Id)
            .FirstOrDefault();
        return matchingRecord;
    }

    private static string[] ReadReasonCodes(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static Dictionary<string, string> ReadMetadata(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ??
                new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static string? GetMetadata(IReadOnlyDictionary<string, string> metadata, string key)
    {
        return metadata.TryGetValue(key, out string? value) ? value : null;
    }

    private static int? TryReadInt32(IReadOnlyDictionary<string, string> metadata, string key)
    {
        return GetMetadata(metadata, key) is { } value && int.TryParse(value, out int result) ? result : null;
    }

    private static DateTimeOffset ToUtcOffset(DateTime value)
    {
        DateTime utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => throw new ArgumentOutOfRangeException(nameof(value), value.Kind, "Unsupported date-time kind.")
        };

        return new DateTimeOffset(utc, TimeSpan.Zero);
    }
}

/// <summary>
/// Represents a governed request's ordered, minimized evidence timeline.
/// </summary>
/// <param name="CorrelationId">The exact request correlation identifier.</param>
/// <param name="Entries">The evidence entries ordered by occurrence time.</param>
public sealed record GovernanceEvidenceTimelineResponse(
    string CorrelationId,
    IReadOnlyList<GovernanceEvidenceTimelineEntry> Entries);

/// <summary>
/// Represents one minimized item in a governed request's evidence timeline.
/// </summary>
/// <param name="Kind">The evidence category.</param>
/// <param name="OccurredUtc">When this evidence item occurred.</param>
/// <param name="EvidenceId">The evidence item's stable identifier.</param>
/// <param name="DecisionAuditRecordId">The linked governance decision record identifier.</param>
/// <param name="DecisionReceiptId">The linked decision receipt identifier.</param>
/// <param name="OperationName">The governed operation name.</param>
/// <param name="Outcome">The decision, mutation, or persistence outcome.</param>
/// <param name="Status">The persistence or lifecycle status.</param>
/// <param name="ReasonCodes">Stable reason codes.</param>
/// <param name="PolicyVersion">The evaluated policy version.</param>
/// <param name="PolicyHash">The evaluated policy hash.</param>
/// <param name="ActorId">The stable actor identifier.</param>
/// <param name="ActorType">The actor type.</param>
/// <param name="OperationExecutionId">The linked governed execution identifier.</param>
/// <param name="MutationBatchId">The linked NCAT mutation batch identifier.</param>
/// <param name="RecordCount">The relevant mutation-audit record count.</param>
/// <param name="EvidenceHash">The ledger record or mutation manifest hash.</param>
/// <param name="EvidenceHashAlgorithm">The evidence hash algorithm, when recorded.</param>
public sealed record GovernanceEvidenceTimelineEntry(
    string Kind,
    DateTimeOffset OccurredUtc,
    string EvidenceId,
    string? DecisionAuditRecordId,
    string? DecisionReceiptId,
    string? OperationName,
    string? Outcome,
    string? Status,
    IReadOnlyList<string> ReasonCodes,
    string? PolicyVersion,
    string? PolicyHash,
    string? ActorId,
    string? ActorType,
    string? OperationExecutionId,
    string? MutationBatchId,
    int? RecordCount,
    string? EvidenceHash,
    string? EvidenceHashAlgorithm);
