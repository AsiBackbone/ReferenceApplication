using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ReferenceApplication.Infrastructure.Data.Entities;

namespace ReferenceApplication.Infrastructure.Data;

/// <summary>
/// Identifies the rows that track mutation-audit evidence itself: audit-completion outbox entries and reconciliation
/// findings and remediations.
/// </summary>
/// <remarks>
/// <para>
/// Bookkeeping rows describe the delivery and review of mutation-audit batches, so they are not governed mutations. If
/// the save pipeline audited them, each save would open a mutation-audit batch that can never have a completion of its
/// own, and reconciliation would report every staged and every delivered completion as a missing completion. The save
/// pipeline therefore skips mutation auditing for them. Each row still keeps its own status, timestamps, and concurrency
/// stamp, and reconciliation findings and remediations are already written without the save pipeline.
/// </para>
/// <para>
/// Unlike <see cref="AccountabilityRecordTypes" />, this classification affects mutation auditing only. Bookkeeping rows
/// are still canonicalized and timestamp-normalized like application data, so their stored schema is unchanged.
/// </para>
/// <para>
/// Releases up to and including 0.1.0-alpha.11 audited outbox saves. Reconciliation uses
/// <see cref="GetTableNames" /> to recognize those retained batches and does not evaluate them as governed mutations.
/// </para>
/// </remarks>
internal static class AuditBookkeepingRecordTypes
{
    private static readonly Type[] _bookkeepingTypes =
    [
        typeof(ApplicationAuditCompletionOutboxEntry),
        typeof(ApplicationAuditReconciliationFinding),
        typeof(ApplicationAuditReconciliationRemediation)
    ];

    /// <summary>
    /// Returns whether the tracked entity instance is mutation-audit bookkeeping.
    /// </summary>
    /// <param name="entity">The tracked entity.</param>
    /// <returns><see langword="true" /> for outbox entries and reconciliation findings and remediations.</returns>
    internal static bool IsAuditBookkeepingRecord(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return _bookkeepingTypes.Contains(entity.GetType());
    }

    /// <summary>
    /// Returns the table names the model maps the bookkeeping entities to, as the save pipeline records them in
    /// <see cref="AuditRecord.Entity" />.
    /// </summary>
    /// <param name="model">The EF Core model.</param>
    /// <returns>The bookkeeping table names.</returns>
    internal static HashSet<string> GetTableNames(IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return new HashSet<string>(
            _bookkeepingTypes
                .Select(model.FindEntityType)
                .Select(entityType => entityType?.GetTableName())
                .OfType<string>(),
            StringComparer.Ordinal);
    }
}
