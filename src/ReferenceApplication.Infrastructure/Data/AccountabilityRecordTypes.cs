using AsiBackbone.EntityFrameworkCore.Persistence;
using ReferenceApplication.Infrastructure.Data.Entities;

namespace ReferenceApplication.Infrastructure.Data;

/// <summary>
/// Identifies persisted accountability evidence that the application save pipeline must store exactly as created.
/// </summary>
/// <remarks>
/// NCAT mutation-audit records and the AsiBackbone EF Core persistence entities are evidence about application
/// behavior, not application data. They are excluded from persisted-string canonicalization, timestamp normalization,
/// the UTC timestamp precision convention, and mutation auditing, so the stored values match the values that were hashed
/// or signed when the evidence was created, and saving evidence never produces further audit records.
/// </remarks>
internal static class AccountabilityRecordTypes
{
    private static readonly System.Reflection.Assembly _asiBackbonePersistenceAssembly =
        typeof(AuditLedgerRecordEntity).Assembly;

    /// <summary>
    /// Returns whether the CLR type is accountability evidence.
    /// </summary>
    /// <param name="clrType">The entity CLR type.</param>
    /// <returns><see langword="true" /> for NCAT audit records and AsiBackbone persistence entities.</returns>
    internal static bool IsAccountabilityRecord(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);

        return clrType == typeof(AuditRecord) || clrType.Assembly == _asiBackbonePersistenceAssembly;
    }

    /// <summary>
    /// Returns whether the tracked entity instance is accountability evidence.
    /// </summary>
    /// <param name="entity">The tracked entity.</param>
    /// <returns><see langword="true" /> for NCAT audit records and AsiBackbone persistence entities.</returns>
    internal static bool IsAccountabilityRecord(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return IsAccountabilityRecord(entity.GetType());
    }
}
