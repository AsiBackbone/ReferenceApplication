using AsiBackbone.Core.Actors;
using AsiBackbone.Core.Audit;
using AsiBackbone.Core.Decisions;
using AsiBackbone.Core.Results;
using AsiBackbone.EntityFrameworkCore.Audit;
using AsiBackbone.EntityFrameworkCore.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReferenceApplication.Infrastructure.Data;
using ReferenceApplication.Infrastructure.Data.Entities;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies how the host-owned <see cref="ApplicationDbContext" /> stores AsiBackbone accountability evidence alongside
/// NCAT application data (walkthrough Step 6).
/// </summary>
public sealed class AccountabilityRecordPersistenceTests
{
    // Characters NCAT's persisted-string canonicalization would rewrite: an HTML entity it decodes and a
    // decomposed "e" + combining acute accent it composes to NFC.
    private const string _evidenceText = "R&amp;D Réviewer";
    private const string _canonicalText = "R&D Réviewer";

    /// <summary>
    /// Verifies which entity types are treated as accountability evidence.
    /// </summary>
    /// <param name="clrType">The entity type.</param>
    /// <param name="expected">Whether the type is accountability evidence.</param>
    [Theory]
    [InlineData(typeof(AuditRecord), true)]
    [InlineData(typeof(AuditLedgerRecordEntity), true)]
    [InlineData(typeof(AuditLedgerMetadataEntity), true)]
    [InlineData(typeof(DecisionReceiptLifecycleEventEntity), true)]
    [InlineData(typeof(GovernanceOutboxEntryEntity), true)]
    [InlineData(typeof(GovernedOperationRecord), false)]
    [InlineData(typeof(ExternalLoginAccount), false)]
    [InlineData(typeof(ApplicationAuditCompletionOutboxEntry), false)]
    public void IsAccountabilityRecord_ClassifiesEntityTypes(Type clrType, bool expected)
    {
        Assert.Equal(expected, AccountabilityRecordTypes.IsAccountabilityRecord(clrType));
    }

    /// <summary>
    /// Verifies argument validation for both overloads.
    /// </summary>
    [Fact]
    public void IsAccountabilityRecord_NullArgument_Throws()
    {
        _ = Assert.Throws<ArgumentNullException>(() => AccountabilityRecordTypes.IsAccountabilityRecord(null!));
        _ = Assert.Throws<ArgumentNullException>(() => AccountabilityRecordTypes.IsAccountabilityRecord((object)null!));
    }

    /// <summary>
    /// Verifies that the host model includes the AsiBackbone tables and the Step 6 governed record table.
    /// </summary>
    /// <param name="clrType">The mapped entity type.</param>
    /// <param name="tableName">The expected table name.</param>
    [Theory]
    [InlineData(typeof(AuditLedgerRecordEntity), "AsiBackboneAuditLedgerRecords")]
    [InlineData(typeof(AuditLedgerReasonCodeEntity), "AsiBackboneAuditLedgerReasonCodes")]
    [InlineData(typeof(AuditLedgerMetadataEntity), "AsiBackboneAuditLedgerMetadata")]
    [InlineData(typeof(DecisionReceiptLifecycleEventEntity), "AsiBackboneAuditResidueLifecycleEvents")]
    [InlineData(typeof(GovernanceOutboxEntryEntity), "AsiBackboneGovernanceOutboxEntries")]
    [InlineData(typeof(GovernedOperationRecord), "GovernedOperationRecords")]
    public async Task Model_MapsGovernanceEvidenceTables(Type clrType, string tableName)
    {
        await using TestApplicationServices app = await TestApplicationServices.CreateAsync(migrate: false);
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        Assert.Equal(tableName, dbContext.Model.FindEntityType(clrType)?.GetTableName());
    }

    /// <summary>
    /// Verifies that the migration history applies to an empty database, includes the Step 6 migration, and matches
    /// the current model, so no model change was left without a migration.
    /// </summary>
    [Fact]
    public async Task Migrations_ApplyCleanly_AndMatchCurrentModel()
    {
        await using TestApplicationServices app = await TestApplicationServices.CreateAsync();
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        Assert.Empty(await dbContext.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.Contains(
            (await dbContext.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken))
                .Select(static migration => migration[(migration.IndexOf('_', StringComparison.Ordinal) + 1)..]),
            static name => name == "AddGovernedAuditEvidence");
        Assert.False(dbContext.Database.HasPendingModelChanges());
    }

    /// <summary>
    /// Verifies that persisting AsiBackbone evidence through the host context writes no NCAT mutation-audit records and
    /// stores strings and timestamps exactly as created, so stored evidence matches what was hashed or signed.
    /// </summary>
    [Fact]
    public async Task SavingAsiBackboneEvidence_IsStoredExactly_AndNotMutationAudited()
    {
        await using TestApplicationServices app = await TestApplicationServices.CreateAsync();
        DateTimeOffset occurredUtc = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_567);
        var record = AuditLedgerRecord.FromDecisionReceipt(
            DecisionReceipt.FromDecision(
                GovernanceActorContext.Human("user-123", _evidenceText),
                "evidence.persistence.test",
                GovernanceDecision.Allow(correlationId: "correlation-1"),
                occurredUtc: occurredUtc,
                metadata: new Dictionary<string, string>(StringComparer.Ordinal) { ["note"] = _evidenceText }));

        await using (AsyncServiceScope writeScope = app.Services.CreateAsyncScope())
        {
            ApplicationDbContext dbContext = writeScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            OperationResult<AuditLedgerRecord> result = await new EfCoreAuditLedgerStore(dbContext)
                .AppendAsync(record, TestContext.Current.CancellationToken);

            Assert.True(result.Succeeded);
        }

        await using AsyncServiceScope readScope = app.Services.CreateAsyncScope();
        ApplicationDbContext readContext = readScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        AuditLedgerRecord? stored = await new EfCoreAuditLedgerStore(readContext)
            .FindByRecordIdAsync(record.RecordId, TestContext.Current.CancellationToken);

        Assert.NotNull(stored);
        Assert.Equal(_evidenceText, stored.ActorDisplayName);
        Assert.Equal(_evidenceText, stored.Metadata["note"]);
        Assert.Equal(occurredUtc, stored.OccurredUtc);
        Assert.Equal(0, await readContext.AuditRecords.CountAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies that ordinary application data in the same context is still canonicalized, timestamp-normalized, and
    /// mutation-audited, so the evidence exclusion did not change NCAT's behavior for application data.
    /// </summary>
    [Fact]
    public async Task SavingApplicationData_IsStillCanonicalizedNormalizedAndAudited()
    {
        await using TestApplicationServices app = await TestApplicationServices.CreateAsync();
        DateTimeOffset createdUtc = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_567);
        var record = new GovernedOperationRecord
        {
            OperationName = "evidence.persistence.test",
            Title = _evidenceText,
            CreatedUtc = createdUtc
        };

        await using (AsyncServiceScope writeScope = app.Services.CreateAsyncScope())
        {
            ApplicationDbContext dbContext = writeScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            _ = dbContext.GovernedOperationRecords.Add(record);
            _ = await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using AsyncServiceScope readScope = app.Services.CreateAsyncScope();
        ApplicationDbContext readContext = readScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        GovernedOperationRecord stored = await readContext.GovernedOperationRecords
            .SingleAsync(x => x.Id == record.Id, TestContext.Current.CancellationToken);
        AuditRecord audit = await readContext.AuditRecords
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(_canonicalText, stored.Title);
        Assert.Equal(createdUtc.AddTicks(-(createdUtc.Ticks % TimeSpan.TicksPerMillisecond)), stored.CreatedUtc);
        Assert.Contains("GovernedOperationRecord", audit.Entity, StringComparison.Ordinal);
        Assert.Equal("Added", audit.State);
    }
}
