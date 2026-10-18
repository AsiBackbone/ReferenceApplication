using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReferenceApplication.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGovernedAuditEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AsiBackboneAuditLedgerRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecordId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SchemaVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EventId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DecisionReceiptId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    OccurredUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RecordedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ActorType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ActorDisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    OperationName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ReasonCodesJson = table.Column<string>(type: "TEXT", maxLength: 65536, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    TraceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    SpanId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ParentSpanId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DecisionLatencyMs = table.Column<long>(type: "INTEGER", nullable: true),
                    ConstraintSetHash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ConstraintCount = table.Column<int>(type: "INTEGER", nullable: true),
                    RiskScore = table.Column<double>(type: "REAL", nullable: true),
                    PolicyScope = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    TenantHash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    OrganizationHash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    EmitterStatus = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    EmitterProvider = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    OutboxSequence = table.Column<long>(type: "INTEGER", nullable: true),
                    GatewayExecutionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DecisionStage = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PolicyVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PolicyHash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    HandshakeId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    AcknowledgmentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CapabilityTokenId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PreviousRecordHash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    RecordHash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    SigningHash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    SignatureKeyId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    SignatureKeyVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    SignatureAlgorithm = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    SignatureValue = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: true),
                    SignatureProvider = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    SignedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    MetadataJson = table.Column<string>(type: "TEXT", maxLength: 65536, nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AsiBackboneAuditLedgerRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AsiBackboneAuditResidueLifecycleEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    StageSequence = table.Column<int>(type: "INTEGER", nullable: false),
                    OccurredUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DecisionReceiptId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    TraceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    OperationName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AsiBackboneAuditResidueLifecycleEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AsiBackboneGovernanceOutboxEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OutboxEntryId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    DeliveredUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxRetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NextRetryUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ProviderName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ProviderRecordId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DeadLetterReason = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LastErrorMessage = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    LastErrorIsRetryable = table.Column<bool>(type: "INTEGER", nullable: true),
                    LastErrorProviderName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LastErrorProviderErrorCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    MetadataJson = table.Column<string>(type: "TEXT", maxLength: 65536, nullable: false),
                    ClaimOwner = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ClaimToken = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ClaimedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ClaimExpiresUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ClaimAttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    EnvelopeId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    EnvelopeSchemaVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EnvelopeEventType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    EnvelopeEventId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    EnvelopeOccurredUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    EnvelopeCreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    EnvelopeCorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    EnvelopeDecisionReceiptId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    EnvelopeLifecycleStage = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    EnvelopeLifecycleStageSequence = table.Column<int>(type: "INTEGER", nullable: true),
                    EnvelopePolicyVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    EnvelopePolicyHash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    EnvelopeTraceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    EnvelopeSpanId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    EnvelopeParentSpanId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    EnvelopeOperationName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    EnvelopeOutcome = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    EnvelopeActorId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    EnvelopeEmitterStatus = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    EnvelopeEmitterProvider = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    EnvelopeOutboxSequence = table.Column<long>(type: "INTEGER", nullable: true),
                    EnvelopeGatewayExecutionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    EnvelopeDecisionStage = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    EnvelopeMetadataJson = table.Column<string>(type: "TEXT", maxLength: 65536, nullable: false),
                    EnvelopePayloadType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    EnvelopePayloadSchemaVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    EnvelopePayloadContentType = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    EnvelopePayloadContentHash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    EnvelopePayloadSizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    EnvelopePayloadMetadataJson = table.Column<string>(type: "TEXT", maxLength: 65536, nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AsiBackboneGovernanceOutboxEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AsiBackboneHandshakeAcknowledgments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AcknowledgmentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SchemaVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    HandshakeId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ActorType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ActorDisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AcknowledgmentCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Acknowledged = table.Column<bool>(type: "INTEGER", nullable: false),
                    OccurredUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    TraceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AsiBackboneHandshakeAcknowledgments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AsiBackboneHandshakeRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    HandshakeId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SchemaVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ActorType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ActorDisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    OperationName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ReasonCode = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    RequiredAcknowledgmentCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RequiredAcknowledgmentText = table.Column<string>(type: "TEXT", nullable: false),
                    RiskLevel = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RiskCategory = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    TraceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PolicyVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PolicyHash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AsiBackboneHandshakeRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GovernedOperationRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OperationName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", precision: 3, nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GovernedOperationRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AsiBackboneAuditLedgerMetadata",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AuditLedgerRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MetadataKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    MetadataValue = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AsiBackboneAuditLedgerMetadata", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AsiBackboneAuditLedgerMetadata_AsiBackboneAuditLedgerRecords_AuditLedgerRecordId",
                        column: x => x.AuditLedgerRecordId,
                        principalTable: "AsiBackboneAuditLedgerRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AsiBackboneAuditLedgerReasonCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AuditLedgerRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    ReasonCode = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AsiBackboneAuditLedgerReasonCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AsiBackboneAuditLedgerReasonCodes_AsiBackboneAuditLedgerRecords_AuditLedgerRecordId",
                        column: x => x.AuditLedgerRecordId,
                        principalTable: "AsiBackboneAuditLedgerRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AsiBackboneHandshakeAcknowledgmentMetadata",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AcknowledgmentResponseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MetadataKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    MetadataValue = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AsiBackboneHandshakeAcknowledgmentMetadata", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AsiBackboneHandshakeAcknowledgmentMetadata_AsiBackboneHandshakeAcknowledgments_AcknowledgmentResponseId",
                        column: x => x.AcknowledgmentResponseId,
                        principalTable: "AsiBackboneHandshakeAcknowledgments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AsiBackboneHandshakeRequestMetadata",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AcknowledgmentRequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MetadataKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    MetadataValue = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AsiBackboneHandshakeRequestMetadata", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AsiBackboneHandshakeRequestMetadata_AsiBackboneHandshakeRequests_AcknowledgmentRequestId",
                        column: x => x.AcknowledgmentRequestId,
                        principalTable: "AsiBackboneHandshakeRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneAuditLedgerMetadata_AuditLedgerRecordId",
                table: "AsiBackboneAuditLedgerMetadata",
                column: "AuditLedgerRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneAuditLedgerMetadata_AuditLedgerRecordId_MetadataKey",
                table: "AsiBackboneAuditLedgerMetadata",
                columns: new[] { "AuditLedgerRecordId", "MetadataKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneAuditLedgerMetadata_MetadataKey",
                table: "AsiBackboneAuditLedgerMetadata",
                column: "MetadataKey");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneAuditLedgerReasonCodes_AuditLedgerRecordId",
                table: "AsiBackboneAuditLedgerReasonCodes",
                column: "AuditLedgerRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneAuditLedgerReasonCodes_AuditLedgerRecordId_ReasonCode",
                table: "AsiBackboneAuditLedgerReasonCodes",
                columns: new[] { "AuditLedgerRecordId", "ReasonCode" });

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneAuditLedgerReasonCodes_AuditLedgerRecordId_Sequence",
                table: "AsiBackboneAuditLedgerReasonCodes",
                columns: new[] { "AuditLedgerRecordId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneAuditLedgerReasonCodes_ReasonCode",
                table: "AsiBackboneAuditLedgerReasonCodes",
                column: "ReasonCode");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneAuditLedgerRecords_ActorId_RecordedUtc_RecordId",
                table: "AsiBackboneAuditLedgerRecords",
                columns: new[] { "ActorId", "RecordedUtc", "RecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneAuditLedgerRecords_CorrelationId_RecordedUtc_RecordId",
                table: "AsiBackboneAuditLedgerRecords",
                columns: new[] { "CorrelationId", "RecordedUtc", "RecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneAuditLedgerRecords_PreviousRecordHash",
                table: "AsiBackboneAuditLedgerRecords",
                column: "PreviousRecordHash");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneAuditLedgerRecords_RecordedUtc_RecordId",
                table: "AsiBackboneAuditLedgerRecords",
                columns: new[] { "RecordedUtc", "RecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneAuditLedgerRecords_RecordId",
                table: "AsiBackboneAuditLedgerRecords",
                column: "RecordId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneAuditLedgerRecords_TraceId_RecordedUtc_RecordId",
                table: "AsiBackboneAuditLedgerRecords",
                columns: new[] { "TraceId", "RecordedUtc", "RecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneAuditResidueLifecycleEvents_CorrelationId",
                table: "AsiBackboneAuditResidueLifecycleEvents",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneAuditResidueLifecycleEvents_CorrelationId_OccurredUtc",
                table: "AsiBackboneAuditResidueLifecycleEvents",
                columns: new[] { "CorrelationId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneAuditResidueLifecycleEvents_DecisionReceiptId",
                table: "AsiBackboneAuditResidueLifecycleEvents",
                column: "DecisionReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneAuditResidueLifecycleEvents_DecisionReceiptId_OccurredUtc",
                table: "AsiBackboneAuditResidueLifecycleEvents",
                columns: new[] { "DecisionReceiptId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneAuditResidueLifecycleEvents_EventId",
                table: "AsiBackboneAuditResidueLifecycleEvents",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneAuditResidueLifecycleEvents_OccurredUtc",
                table: "AsiBackboneAuditResidueLifecycleEvents",
                column: "OccurredUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneAuditResidueLifecycleEvents_OperationName",
                table: "AsiBackboneAuditResidueLifecycleEvents",
                column: "OperationName");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneAuditResidueLifecycleEvents_Outcome",
                table: "AsiBackboneAuditResidueLifecycleEvents",
                column: "Outcome");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneAuditResidueLifecycleEvents_Stage",
                table: "AsiBackboneAuditResidueLifecycleEvents",
                column: "Stage");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneAuditResidueLifecycleEvents_StageSequence",
                table: "AsiBackboneAuditResidueLifecycleEvents",
                column: "StageSequence");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneAuditResidueLifecycleEvents_TraceId",
                table: "AsiBackboneAuditResidueLifecycleEvents",
                column: "TraceId");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneGovernanceOutboxEntries_ClaimToken",
                table: "AsiBackboneGovernanceOutboxEntries",
                column: "ClaimToken");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneGovernanceOutboxEntries_OutboxEntryId",
                table: "AsiBackboneGovernanceOutboxEntries",
                column: "OutboxEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneGovernanceOutboxEntries_Status_ClaimExpiresUtc_CreatedUtc_OutboxEntryId",
                table: "AsiBackboneGovernanceOutboxEntries",
                columns: new[] { "Status", "ClaimExpiresUtc", "CreatedUtc", "OutboxEntryId" });

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneGovernanceOutboxEntries_Status_ClaimExpiresUtc_NextRetryUtc_UpdatedUtc_OutboxEntryId",
                table: "AsiBackboneGovernanceOutboxEntries",
                columns: new[] { "Status", "ClaimExpiresUtc", "NextRetryUtc", "UpdatedUtc", "OutboxEntryId" });

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneGovernanceOutboxEntries_Status_CreatedUtc_OutboxEntryId",
                table: "AsiBackboneGovernanceOutboxEntries",
                columns: new[] { "Status", "CreatedUtc", "OutboxEntryId" });

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneGovernanceOutboxEntries_Status_NextRetryUtc_UpdatedUtc_OutboxEntryId",
                table: "AsiBackboneGovernanceOutboxEntries",
                columns: new[] { "Status", "NextRetryUtc", "UpdatedUtc", "OutboxEntryId" });

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeAcknowledgmentMetadata_AcknowledgmentResponseId",
                table: "AsiBackboneHandshakeAcknowledgmentMetadata",
                column: "AcknowledgmentResponseId");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeAcknowledgmentMetadata_AcknowledgmentResponseId_MetadataKey",
                table: "AsiBackboneHandshakeAcknowledgmentMetadata",
                columns: new[] { "AcknowledgmentResponseId", "MetadataKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeAcknowledgmentMetadata_MetadataKey",
                table: "AsiBackboneHandshakeAcknowledgmentMetadata",
                column: "MetadataKey");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeAcknowledgments_Acknowledged",
                table: "AsiBackboneHandshakeAcknowledgments",
                column: "Acknowledged");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeAcknowledgments_AcknowledgmentCode",
                table: "AsiBackboneHandshakeAcknowledgments",
                column: "AcknowledgmentCode");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeAcknowledgments_AcknowledgmentId",
                table: "AsiBackboneHandshakeAcknowledgments",
                column: "AcknowledgmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeAcknowledgments_ActorId",
                table: "AsiBackboneHandshakeAcknowledgments",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeAcknowledgments_ActorId_OccurredUtc",
                table: "AsiBackboneHandshakeAcknowledgments",
                columns: new[] { "ActorId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeAcknowledgments_ActorType",
                table: "AsiBackboneHandshakeAcknowledgments",
                column: "ActorType");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeAcknowledgments_CorrelationId",
                table: "AsiBackboneHandshakeAcknowledgments",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeAcknowledgments_CorrelationId_OccurredUtc",
                table: "AsiBackboneHandshakeAcknowledgments",
                columns: new[] { "CorrelationId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeAcknowledgments_HandshakeId",
                table: "AsiBackboneHandshakeAcknowledgments",
                column: "HandshakeId");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeAcknowledgments_HandshakeId_OccurredUtc",
                table: "AsiBackboneHandshakeAcknowledgments",
                columns: new[] { "HandshakeId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeAcknowledgments_OccurredUtc",
                table: "AsiBackboneHandshakeAcknowledgments",
                column: "OccurredUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeAcknowledgments_SchemaVersion",
                table: "AsiBackboneHandshakeAcknowledgments",
                column: "SchemaVersion");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeAcknowledgments_TraceId",
                table: "AsiBackboneHandshakeAcknowledgments",
                column: "TraceId");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeRequestMetadata_AcknowledgmentRequestId",
                table: "AsiBackboneHandshakeRequestMetadata",
                column: "AcknowledgmentRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeRequestMetadata_AcknowledgmentRequestId_MetadataKey",
                table: "AsiBackboneHandshakeRequestMetadata",
                columns: new[] { "AcknowledgmentRequestId", "MetadataKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeRequestMetadata_MetadataKey",
                table: "AsiBackboneHandshakeRequestMetadata",
                column: "MetadataKey");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeRequests_ActorId",
                table: "AsiBackboneHandshakeRequests",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeRequests_ActorId_OperationName",
                table: "AsiBackboneHandshakeRequests",
                columns: new[] { "ActorId", "OperationName" });

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeRequests_ActorType",
                table: "AsiBackboneHandshakeRequests",
                column: "ActorType");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeRequests_CorrelationId",
                table: "AsiBackboneHandshakeRequests",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeRequests_CorrelationId_OperationName",
                table: "AsiBackboneHandshakeRequests",
                columns: new[] { "CorrelationId", "OperationName" });

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeRequests_HandshakeId",
                table: "AsiBackboneHandshakeRequests",
                column: "HandshakeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeRequests_OperationName",
                table: "AsiBackboneHandshakeRequests",
                column: "OperationName");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeRequests_PolicyHash",
                table: "AsiBackboneHandshakeRequests",
                column: "PolicyHash");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeRequests_PolicyVersion",
                table: "AsiBackboneHandshakeRequests",
                column: "PolicyVersion");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeRequests_PolicyVersion_PolicyHash",
                table: "AsiBackboneHandshakeRequests",
                columns: new[] { "PolicyVersion", "PolicyHash" });

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeRequests_ReasonCode",
                table: "AsiBackboneHandshakeRequests",
                column: "ReasonCode");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeRequests_RequiredAcknowledgmentCode",
                table: "AsiBackboneHandshakeRequests",
                column: "RequiredAcknowledgmentCode");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeRequests_RiskCategory",
                table: "AsiBackboneHandshakeRequests",
                column: "RiskCategory");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeRequests_RiskLevel",
                table: "AsiBackboneHandshakeRequests",
                column: "RiskLevel");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeRequests_SchemaVersion",
                table: "AsiBackboneHandshakeRequests",
                column: "SchemaVersion");

            migrationBuilder.CreateIndex(
                name: "IX_AsiBackboneHandshakeRequests_TraceId",
                table: "AsiBackboneHandshakeRequests",
                column: "TraceId");

            migrationBuilder.CreateIndex(
                name: "IX_GovernedOperationRecords_CreatedUtc",
                table: "GovernedOperationRecords",
                column: "CreatedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AsiBackboneAuditLedgerMetadata");

            migrationBuilder.DropTable(
                name: "AsiBackboneAuditLedgerReasonCodes");

            migrationBuilder.DropTable(
                name: "AsiBackboneAuditResidueLifecycleEvents");

            migrationBuilder.DropTable(
                name: "AsiBackboneGovernanceOutboxEntries");

            migrationBuilder.DropTable(
                name: "AsiBackboneHandshakeAcknowledgmentMetadata");

            migrationBuilder.DropTable(
                name: "AsiBackboneHandshakeRequestMetadata");

            migrationBuilder.DropTable(
                name: "GovernedOperationRecords");

            migrationBuilder.DropTable(
                name: "AsiBackboneAuditLedgerRecords");

            migrationBuilder.DropTable(
                name: "AsiBackboneHandshakeAcknowledgments");

            migrationBuilder.DropTable(
                name: "AsiBackboneHandshakeRequests");
        }
    }
}
