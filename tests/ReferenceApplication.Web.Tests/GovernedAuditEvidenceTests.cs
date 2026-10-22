using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using AsiBackbone.Core.Audit;
using AsiBackbone.Core.Decisions;
using AsiBackbone.Core.Emissions;
using AsiBackbone.Core.HostIntegration;
using AsiBackbone.EntityFrameworkCore.Audit;
using AsiBackbone.EntityFrameworkCore.Persistence;
using AsiBackbone.OpenTelemetry;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ReferenceApplication.Infrastructure.Data;
using ReferenceApplication.Infrastructure.Data.Auditing;
using ReferenceApplication.Infrastructure.Data.Entities;
using ReferenceApplication.Web.Authentication.Claims;
using ReferenceApplication.Web.Controllers.Api;
using ReferenceApplication.Web.Governance;
using ReferenceApplication.Web.Tests.Extensions;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies walkthrough Step 6 end to end through the complete web host and a migrated database: a governed request
/// persists its decision to the AsiBackbone audit ledger, its NCAT mutation audit references that decision, and the
/// NCAT audit-completion outbox delivers a linked AsiBackbone lifecycle event.
/// </summary>
public sealed class GovernedAuditEvidenceTests : IDisposable
{
    private const string _testAuthenticationScheme = "GovernedAuditEvidenceTestAuthentication";
    private const string _testUserHeaderName = "X-Test-User";
    private const string _testRoleHeaderName = "X-Test-Role";
    private const string _testPermissionHeaderName = "X-Test-Permission";
    private const string _antiforgeryFormFieldName = "__RequestVerificationToken";
    private const string _basePath = "/api/v1/governed-operations";
    private static readonly JsonSerializerOptions _webJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TestSqliteDatabase _database = TestSqliteDatabase.Create();

    /// <summary>
    /// Verifies the complete evidence chain for an allowed governed mutation: decision receipt, application record,
    /// correlated NCAT mutation audit, staged completion, and the delivered AsiBackbone lifecycle event.
    /// </summary>
    [Fact]
    public async Task CreateRecord_Administrator_LinksDecisionMutationAndCompletionEvidence()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostAsync(
            client,
            "records",
            role: "administrator",
            form: new Dictionary<string, string> { ["Title"] = "Quarterly review" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        GovernedRecordCreatedResponse created = Assert.IsType<GovernedRecordCreatedResponse>(
            await response.Content.ReadFromJsonAsync<GovernedRecordCreatedResponse>(TestContext.Current.CancellationToken));

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // 1. The governance decision was persisted to the AsiBackbone audit ledger, with the Step 4 policy identity.
        AuditLedgerRecord decision = Assert.IsType<AuditLedgerRecord>(
            await new EfCoreAuditLedgerStore(dbContext).FindByRecordIdAsync(
                created.DecisionAuditRecordId,
                TestContext.Current.CancellationToken));
        Assert.Equal(GovernanceDecisionOutcome.Allowed.ToString(), decision.Outcome);
        Assert.Equal("test-admin", decision.ActorId);
        Assert.Equal(ReferenceApplicationGovernancePolicySet.Version, decision.PolicyVersion);
        Assert.Equal(ReferenceApplicationGovernancePolicySet.PolicyHash, decision.PolicyHash);
        Assert.Equal(1, await dbContext.AsiBackboneAuditLedgerRecords.CountAsync(TestContext.Current.CancellationToken));

        // 2. The application record exists, created by the governed operation.
        GovernedOperationRecord record = await dbContext.GovernedOperationRecords
            .SingleAsync(x => x.Id == created.RecordId, TestContext.Current.CancellationToken);
        Assert.Equal("Quarterly review", record.Title);
        Assert.Equal(decision.OperationName, record.OperationName);

        // 3. The NCAT mutation audit references the decision and shares its execution, correlation, and trace identity.
        AuditRecord mutationAudit = await dbContext.AuditRecords
            .SingleAsync(x => x.MutationBatchId == created.MutationBatchId, TestContext.Current.CancellationToken);
        Assert.Equal(decision.RecordId, mutationAudit.DecisionAuditRecordId);
        Assert.Equal(created.OperationExecutionId, mutationAudit.OperationExecutionId);
        Assert.Equal(decision.CorrelationId, mutationAudit.CorrelationId);
        Assert.Equal(decision.TraceId, mutationAudit.TraceId);
        Assert.Equal("test-admin", mutationAudit.ActorId);

        // 4. The same transaction staged an audit-completion entry for the AsiBackbone destination.
        ApplicationAuditCompletionOutboxEntry outboxEntry = await dbContext.ApplicationAuditCompletionOutboxEntries
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AsiBackboneAuditCompletionPublisher.DestinationName, outboxEntry.Destination);
        Assert.Equal(ApplicationAuditCompletionOutboxStatuses.Pending, outboxEntry.Status);
        Assert.Equal(created.MutationBatchId, outboxEntry.MutationBatchId);
        Assert.Equal(decision.RecordId, outboxEntry.DecisionAuditRecordId);

        // 5. Dispatching the outbox appends the AsiBackbone lifecycle event that closes the loop.
        ApplicationAuditCompletionDispatchSummary summary = await DispatchAsync(factory);
        Assert.Equal(1, summary.DeliveredCount);

        DecisionReceiptLifecycleEvent lifecycleEvent = Assert.IsType<DecisionReceiptLifecycleEvent>(
            await new EfCoreDecisionReceiptLifecycleStore(dbContext).FindByEventIdAsync(
                AsiBackboneAuditCompletionPublisher.CreateLifecycleEventId(outboxEntry.IdempotencyKey),
                TestContext.Current.CancellationToken));
        Assert.Equal(DecisionReceiptLifecycleStage.GatewayExecutionCompleted, lifecycleEvent.Stage);
        Assert.Equal(decision.CorrelationId, lifecycleEvent.CorrelationId);
        Assert.Equal(decision.RecordId, lifecycleEvent.Metadata[HostAccountabilityMetadataKeys.DecisionAuditRecordId]);
        Assert.Equal(created.OperationExecutionId, lifecycleEvent.Metadata[HostAccountabilityMetadataKeys.OperationExecutionId]);
        Assert.Equal(created.MutationBatchId, lifecycleEvent.Metadata[HostAccountabilityMetadataKeys.MutationBatchId]);
        Assert.Equal("1", lifecycleEvent.Metadata[HostAccountabilityMetadataKeys.MutationRecordCount]);

        dbContext.ChangeTracker.Clear();
        Assert.Equal(
            ApplicationAuditCompletionOutboxStatuses.Delivered,
            (await dbContext.ApplicationAuditCompletionOutboxEntries.SingleAsync(TestContext.Current.CancellationToken)).Status);

        // 6. A later dispatch finds nothing left to deliver and writes no second event.
        Assert.Equal(0, (await DispatchAsync(factory)).AttemptedCount);
        Assert.Equal(1, await dbContext.AsiBackboneDecisionReceiptLifecycleEvents.CountAsync(TestContext.Current.CancellationToken));

        // 7. The administrator-only query returns the complete evidence chain as one minimized, ordered timeline.
        string correlationId = Assert.IsType<string>(decision.CorrelationId);
        using HttpResponseMessage evidenceResponse = await GetEvidenceAsync(client, correlationId);
        Assert.Equal(HttpStatusCode.OK, evidenceResponse.StatusCode);
        string evidenceJson = await evidenceResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        GovernanceEvidenceTimelineResponse timeline = Assert.IsType<GovernanceEvidenceTimelineResponse>(
            JsonSerializer.Deserialize<GovernanceEvidenceTimelineResponse>(
                evidenceJson,
                _webJsonOptions));

        Assert.Equal(correlationId, timeline.CorrelationId);
        Assert.Contains(timeline.Entries, entry => entry.Kind == "decision" && entry.Outcome == "Allowed");
        Assert.Contains(timeline.Entries, entry => entry.Kind == "application-record" && entry.EvidenceId == created.RecordId.ToString("D"));
        Assert.Contains(timeline.Entries, entry => entry.Kind == "mutation-audit" && entry.MutationBatchId == created.MutationBatchId);
        Assert.Contains(timeline.Entries, entry => entry.Kind == "audit-completion" && entry.Status == ApplicationAuditCompletionOutboxStatuses.Delivered);
        Assert.Contains(timeline.Entries, entry => entry.Kind == "lifecycle-event" && entry.Status == DecisionReceiptLifecycleStage.GatewayExecutionCompleted.ToString());
        Assert.Equal(
            timeline.Entries.OrderBy(entry => entry.OccurredUtc)
                .ThenBy(entry => entry.Kind, StringComparer.Ordinal)
                .ThenBy(entry => entry.EvidenceId, StringComparer.Ordinal),
            timeline.Entries);
        Assert.DoesNotContain("Quarterly review", evidenceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("originalValues", evidenceJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("currentValues", evidenceJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("keyValues", evidenceJson, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that, with OpenTelemetry enabled, the host exports AsiBackbone governance telemetry for the evidence
    /// chain: a decision span in the governed request's trace, a lifecycle span when the completion is delivered,
    /// low-cardinality governance metrics, and the reconciliation gauges, and that no actor, reason, metadata, request
    /// value, or mutation value reaches telemetry.
    /// </summary>
    [Fact]
    public async Task CreateRecord_OpenTelemetryEnabled_ExportsGovernanceSpansMetricsAndReconciliationGauges()
    {
        InMemoryTelemetryExporters telemetry = new();
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(configureServices: telemetry.AddTo);
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostAsync(
            client,
            "records",
            role: "administrator",
            form: new Dictionary<string, string> { ["Title"] = "Telemetry review" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        GovernedRecordCreatedResponse created = Assert.IsType<GovernedRecordCreatedResponse>(
            await response.Content.ReadFromJsonAsync<GovernedRecordCreatedResponse>(TestContext.Current.CancellationToken));

        AuditLedgerRecord decision;
        ApplicationAuditCompletionOutboxEntry outboxEntry;
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            decision = Assert.IsType<AuditLedgerRecord>(
                await new EfCoreAuditLedgerStore(dbContext).FindByRecordIdAsync(
                    created.DecisionAuditRecordId,
                    TestContext.Current.CancellationToken));
            outboxEntry = await dbContext.ApplicationAuditCompletionOutboxEntries
                .AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken);
        }

        string correlationId = Assert.IsType<string>(decision.CorrelationId);
        string decisionTraceId = Assert.IsType<string>(decision.TraceId);

        // 1. The decision is projected once, as an internal span in the governed request's trace.
        InMemoryTelemetryExporters.ExportedSpan decisionSpan = Assert.Single(
            GovernanceSpans(telemetry, correlationId),
            static span => span.Tags.GetValueOrDefault(OpenTelemetryGovernanceAttributes.EventType) ==
                nameof(GovernanceEmissionEventType.Decision));
        Assert.Equal(decision.OperationName, decisionSpan.Name);
        Assert.Equal(ActivityKind.Internal, decisionSpan.Kind);
        Assert.NotNull(decisionSpan.ParentSpanId);
        Assert.Contains(decisionSpan.TraceId, decisionTraceId, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GovernanceDecisionOutcome.Allowed.ToString(), decisionSpan.Tags[OpenTelemetryGovernanceAttributes.DecisionOutcome]);
        Assert.Equal(decision.DecisionReceiptId, decisionSpan.Tags[OpenTelemetryGovernanceAttributes.DecisionReceiptId]);
        Assert.Equal(ReferenceApplicationGovernancePolicySet.Version, decisionSpan.Tags[OpenTelemetryGovernanceAttributes.PolicyVersion]);
        Assert.Equal(ReferenceApplicationGovernancePolicySet.PolicyHash, decisionSpan.Tags[OpenTelemetryGovernanceAttributes.PolicyHash]);
        Assert.Contains(OpenTelemetryGovernanceInstrumentation.DecisionEvaluatedEventName, decisionSpan.EventNames);

        // 2. Delivering the completion projects the lifecycle event it appended.
        Assert.Equal(1, (await DispatchAsync(factory)).DeliveredCount);
        InMemoryTelemetryExporters.ExportedSpan lifecycleSpan = Assert.Single(
            GovernanceSpans(telemetry, correlationId),
            static span => span.Tags.GetValueOrDefault(OpenTelemetryGovernanceAttributes.EventType) ==
                nameof(GovernanceEmissionEventType.AuditLifecycle));
        Assert.Equal(decision.OperationName, lifecycleSpan.Name);
        Assert.Equal(decisionSpan.Tags[OpenTelemetryGovernanceAttributes.DecisionReceiptId], lifecycleSpan.Tags[OpenTelemetryGovernanceAttributes.DecisionReceiptId]);
        Assert.Equal(
            nameof(DecisionReceiptLifecycleStage.GatewayExecutionCompleted),
            lifecycleSpan.Tags[OpenTelemetryGovernanceAttributes.LifecycleStage]);
        Assert.Contains(OpenTelemetryGovernanceInstrumentation.LifecycleRecordedEventName, lifecycleSpan.EventNames);

        // 3. Telemetry carries identifiers and outcomes, never the actor, request or mutation values, or metadata.
        string[] neverExported =
        [
            "test-admin",
            "Telemetry review",
            created.RecordId.ToString("D"),
            Assert.IsType<string>(created.MutationBatchId),
            created.OperationExecutionId,
            outboxEntry.IdempotencyKey,
            outboxEntry.MutationManifestHash
        ];
        Assert.All(GovernanceSpans(telemetry, correlationId), span => Assert.All(span.Tags, tag =>
        {
            Assert.DoesNotContain(tag.Value, neverExported);
            Assert.DoesNotContain("actor", tag.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("reason", tag.Key, StringComparison.OrdinalIgnoreCase);
        }));

        // 4. Governance metrics count projections by low-cardinality dimensions only, and the reconciliation gauges
        // are exported alongside them.
        InMemoryTelemetryExporters.CollectMetrics(factory.Services);
        IReadOnlyList<InMemoryTelemetryExporters.ExportedMetricPoint> points = telemetry.MetricPoints;

        foreach (string eventType in new[]
        {
            nameof(GovernanceEmissionEventType.Decision),
            nameof(GovernanceEmissionEventType.AuditLifecycle)
        })
        {
            Assert.Contains(points, point =>
                point.Meter == OpenTelemetryGovernanceInstrumentation.MeterName &&
                point.Instrument == OpenTelemetryGovernanceInstrumentation.EmissionsCounterName &&
                point.Tags.GetValueOrDefault(OpenTelemetryGovernanceAttributes.MetricEventType) == eventType &&
                point.Value >= 1);
        }

        Assert.Contains(points, static point =>
            point.Meter == OpenTelemetryGovernanceInstrumentation.MeterName &&
            point.Instrument == OpenTelemetryGovernanceInstrumentation.EmissionLatencyHistogramName &&
            point.Count >= 1);

        string[] metricDimensions =
        [
            OpenTelemetryGovernanceAttributes.MetricEventType,
            OpenTelemetryGovernanceAttributes.MetricResult,
            OpenTelemetryGovernanceAttributes.MetricProvider,
            OpenTelemetryGovernanceAttributes.MetricFailureCode,
            OpenTelemetryGovernanceAttributes.MetricRetryable
        ];
        Assert.All(
            points.Where(static point => point.Meter == OpenTelemetryGovernanceInstrumentation.MeterName),
            point => Assert.All(point.Tags.Keys, key => Assert.Contains(key, metricDimensions)));

        foreach (string gauge in new[] { "ncat.audit.reconciliation.findings.open", "ncat.audit.outbox.pending" })
        {
            Assert.Contains(points, point =>
                point.Meter == ApplicationAuditReconciliationMetrics.MeterName &&
                point.Instrument == gauge);
        }
    }

    /// <summary>
    /// Verifies that turning governance instrumentation off registers no governance emitter and exports no governance
    /// span or metric, while the governed request and its evidence are unaffected.
    /// </summary>
    [Fact]
    public async Task CreateRecord_GovernanceInstrumentationDisabled_ExportsNoGovernanceTelemetry()
    {
        InMemoryTelemetryExporters telemetry = new();
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(
            configureServices: telemetry.AddTo,
            settings: new Dictionary<string, string?>
            {
                ["ReferenceApplication:OpenTelemetry:EnableGovernanceInstrumentation"] = "false"
            });
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostAsync(
            client,
            "records",
            role: "administrator",
            form: new Dictionary<string, string> { ["Title"] = "Untraced review" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, (await DispatchAsync(factory)).DeliveredCount);
        Assert.Null(factory.Services.GetService<IGovernanceEmitter>());
        Assert.False(factory.Services.GetRequiredService<GovernanceTelemetryProjection>().IsEnabled);

        // The exporters are wired: the request's server span, which ends just after the response, is exported.
        for (int attempt = 0; attempt < 25 && !telemetry.Spans.Any(IsServerSpan); attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        }

        Assert.Contains(telemetry.Spans, IsServerSpan);

        InMemoryTelemetryExporters.CollectMetrics(factory.Services);
        Assert.DoesNotContain(
            telemetry.Spans,
            static span => span.Source == OpenTelemetryGovernanceInstrumentation.ActivitySourceName);
        Assert.DoesNotContain(telemetry.MetricPoints, static point =>
            point.Meter == OpenTelemetryGovernanceInstrumentation.MeterName ||
            point.Meter == ApplicationAuditReconciliationMetrics.MeterName);

        static bool IsServerSpan(InMemoryTelemetryExporters.ExportedSpan span)
        {
            return span.Source == "Microsoft.AspNetCore" && span.Kind == ActivityKind.Server;
        }
    }

    private static IEnumerable<InMemoryTelemetryExporters.ExportedSpan> GovernanceSpans(
        InMemoryTelemetryExporters telemetry,
        string correlationId)
    {
        return telemetry.Spans.Where(span =>
            span.Source == OpenTelemetryGovernanceInstrumentation.ActivitySourceName &&
            span.Tags.GetValueOrDefault(OpenTelemetryGovernanceAttributes.CorrelationId) == correlationId);
    }

    /// <summary>
    /// Verifies that a denied governed mutation leaves a denial receipt but no application change, mutation audit, or
    /// completion entry.
    /// </summary>
    [Fact]
    public async Task CreateRecord_NonAdministrator_RecordsDenialWithoutMutation()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostAsync(
            client,
            "records",
            role: "reader",
            form: new Dictionary<string, string> { ["Title"] = "Should not exist" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        AuditLedgerRecordEntity decision = await dbContext.AsiBackboneAuditLedgerRecords
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(GovernanceDecisionOutcome.Denied.ToString(), decision.Outcome);
        Assert.Contains(AdministratorRoleConstraint.DeniedReasonCode, decision.ReasonCodesJson, StringComparison.Ordinal);
        Assert.Equal(0, await dbContext.GovernedOperationRecords.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await dbContext.AuditRecords.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await dbContext.ApplicationAuditCompletionOutboxEntries.CountAsync(TestContext.Current.CancellationToken));

        using HttpResponseMessage evidenceResponse = await GetEvidenceAsync(client, decision.CorrelationId!);
        Assert.Equal(HttpStatusCode.OK, evidenceResponse.StatusCode);
        GovernanceEvidenceTimelineResponse timeline = Assert.IsType<GovernanceEvidenceTimelineResponse>(
            await evidenceResponse.Content.ReadFromJsonAsync<GovernanceEvidenceTimelineResponse>(
                TestContext.Current.CancellationToken));
        GovernanceEvidenceTimelineEntry denied = Assert.Single(timeline.Entries);
        Assert.Equal("decision", denied.Kind);
        Assert.Equal(GovernanceDecisionOutcome.Denied.ToString(), denied.Outcome);
        Assert.Contains(AdministratorRoleConstraint.DeniedReasonCode, denied.ReasonCodes);
    }

    /// <summary>
    /// Verifies that an unknown correlation identifier does not disclose or synthesize evidence.
    /// </summary>
    [Fact]
    public async Task GovernanceEvidence_UnknownCorrelation_ReturnsNotFound()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await GetEvidenceAsync(client, "unknown-correlation");
        using HttpResponseMessage verification = await GetVerificationAsync(client, "unknown-correlation");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, verification.StatusCode);
    }

    /// <summary>
    /// Verifies that a newly persisted decision carries a canonical hash and that the evidence API can recompute it.
    /// </summary>
    [Fact]
    public async Task GovernanceEvidenceVerification_UntouchedRecord_IsVerified()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateHttpsClient();
        using HttpResponseMessage createResponse = await PostAsync(
            client,
            "records",
            role: "administrator",
            form: new Dictionary<string, string> { ["Title"] = "Verified evidence" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuditLedgerRecordEntity decision = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .AsiBackboneAuditLedgerRecords
            .SingleAsync(TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await GetVerificationAsync(client, decision.CorrelationId!);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GovernanceEvidenceVerificationResponse verification = Assert.IsType<GovernanceEvidenceVerificationResponse>(
            await response.Content.ReadFromJsonAsync<GovernanceEvidenceVerificationResponse>(
                TestContext.Current.CancellationToken));
        GovernanceEvidenceRecordVerification result = Assert.Single(verification.Records);

        Assert.Equal(decision.RecordId, result.RecordId);
        Assert.Equal(GovernanceEvidenceVerificationStatuses.Verified, result.Status);
        Assert.Equal(decision.RecordHash, result.StoredHash);
        Assert.Equal(result.StoredHash, result.ComputedHash);
        Assert.Equal("SHA-256", result.HashAlgorithm);
        Assert.Null(decision.PreviousRecordHash);
    }

    /// <summary>
    /// Verifies that directly changing a stored canonical string, including HTML-entity and Unicode content, causes
    /// verification to report a mismatch.
    /// </summary>
    [Fact]
    public async Task GovernanceEvidenceVerification_AlteredCanonicalText_IsMismatch()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateHttpsClient();
        using HttpResponseMessage createResponse = await PostAsync(client, "administrator", role: "administrator");
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        string correlationId;
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            AuditLedgerRecordEntity decision = await dbContext.AsiBackboneAuditLedgerRecords
                .SingleAsync(TestContext.Current.CancellationToken);
            correlationId = decision.CorrelationId!;
            decision.OperationName = $"{decision.OperationName} &amp; caf\u00e9 \u03a9";
            _ = await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        GovernanceEvidenceRecordVerification result = await GetSingleVerificationAsync(client, correlationId);

        Assert.Equal(GovernanceEvidenceVerificationStatuses.Mismatch, result.Status);
        Assert.NotEqual(result.StoredHash, result.ComputedHash);
    }

    /// <summary>
    /// Verifies that directly changing a stored timestamp below millisecond precision causes verification to report
    /// a mismatch.
    /// </summary>
    [Fact]
    public async Task GovernanceEvidenceVerification_AlteredSubMillisecondTimestamp_IsMismatch()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateHttpsClient();
        using HttpResponseMessage createResponse = await PostAsync(client, "administrator", role: "administrator");
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        string correlationId;
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            AuditLedgerRecordEntity decision = await dbContext.AsiBackboneAuditLedgerRecords
                .SingleAsync(TestContext.Current.CancellationToken);
            correlationId = decision.CorrelationId!;
            decision.RecordedUtc = decision.RecordedUtc.AddTicks(1);
            _ = await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        GovernanceEvidenceRecordVerification result = await GetSingleVerificationAsync(client, correlationId);

        Assert.Equal(GovernanceEvidenceVerificationStatuses.Mismatch, result.Status);
        Assert.NotEqual(result.StoredHash, result.ComputedHash);
    }

    /// <summary>
    /// Verifies that a legacy record without a stored hash is identified without treating it as a failed hash check.
    /// </summary>
    [Fact]
    public async Task GovernanceEvidenceVerification_LegacyRecord_IsNotHashed()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateHttpsClient();
        using HttpResponseMessage createResponse = await PostAsync(client, "administrator", role: "administrator");
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        string correlationId;
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            AuditLedgerRecordEntity decision = await dbContext.AsiBackboneAuditLedgerRecords
                .SingleAsync(TestContext.Current.CancellationToken);
            correlationId = decision.CorrelationId!;
            decision.RecordHash = null;
            _ = await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        GovernanceEvidenceRecordVerification result = await GetSingleVerificationAsync(client, correlationId);

        Assert.Equal(GovernanceEvidenceVerificationStatuses.NotHashed, result.Status);
        Assert.Null(result.StoredHash);
        Assert.Null(result.ComputedHash);
        Assert.Null(result.HashAlgorithm);
    }

    /// <summary>
    /// Verifies that evidence cannot be read anonymously or by a non-administrator.
    /// </summary>
    [Fact]
    public async Task GovernanceEvidence_AccessControl_RejectsAnonymousAndNonAdministrator()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateHttpsClient();
        using HttpResponseMessage createResponse = await PostAsync(
            client,
            "records",
            role: "administrator",
            form: new Dictionary<string, string> { ["Title"] = "Protected evidence" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        string correlationId = (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .AsiBackboneAuditLedgerRecords
            .SingleAsync(TestContext.Current.CancellationToken)).CorrelationId!;

        using HttpResponseMessage anonymous = await GetEvidenceAsync(client, correlationId, user: null, role: null);
        using HttpResponseMessage nonAdministrator = await GetEvidenceAsync(
            client,
            correlationId,
            user: "test-reader",
            role: "reader");
        using HttpResponseMessage anonymousVerification = await GetVerificationAsync(
            client,
            correlationId,
            user: null,
            role: null);
        using HttpResponseMessage nonAdministratorVerification = await GetVerificationAsync(
            client,
            correlationId,
            user: "test-reader",
            role: "reader");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, nonAdministrator.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousVerification.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, nonAdministratorVerification.StatusCode);
    }

    /// <summary>
    /// Verifies that a subscriber receives identifier-only decision, mutation-audit, and lifecycle-event hints in
    /// persistence order, then can re-query the complete persisted timeline.
    /// </summary>
    [Fact]
    public async Task GovernanceEvidenceNotifications_Subscriber_ReceivesPersistedEvidenceInOrder()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateHttpsClient();
        using var disconnected = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        IGovernanceEvidenceNotifier notifier = factory.Services.GetRequiredService<IGovernanceEvidenceNotifier>();
        await using IGovernanceEvidenceSubscription subscription = notifier.Subscribe();
        Task<IReadOnlyList<GovernanceEvidenceNotification>> notificationsTask = ReadNotificationsAsync(
            subscription,
            count: 3,
            disconnected.Token);

        using HttpResponseMessage createResponse = await PostAsync(
            client,
            "records",
            role: "administrator",
            form: new Dictionary<string, string> { ["Title"] = "Live evidence" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal(1, (await DispatchAsync(factory)).DeliveredCount);

        IReadOnlyList<GovernanceEvidenceNotification> notifications = await notificationsTask;
        Assert.Equal(
            [
                GovernanceEvidenceNotificationKinds.Decision,
                GovernanceEvidenceNotificationKinds.MutationAudit,
                GovernanceEvidenceNotificationKinds.LifecycleEvent
            ],
            notifications.Select(notification => notification.Kind));
        Assert.Single(notifications.Select(notification => notification.CorrelationId).Distinct(StringComparer.Ordinal));
        Assert.All(notifications, notification => Assert.False(string.IsNullOrWhiteSpace(notification.RecordId)));

        using HttpResponseMessage evidenceResponse = await GetEvidenceAsync(
            client,
            notifications[0].CorrelationId);
        Assert.Equal(HttpStatusCode.OK, evidenceResponse.StatusCode);

        await disconnected.CancelAsync();
    }

    /// <summary>
    /// Verifies that the notification stream has the same administrator-only access boundary as the evidence query.
    /// </summary>
    [Fact]
    public async Task GovernanceEvidenceNotifications_AccessControl_RejectsAnonymousAndNonAdministrator()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateHttpsClient();
        using HttpRequestMessage anonymousRequest = new(
            HttpMethod.Get,
            "/api/v1/governance-evidence/notifications");
        using HttpRequestMessage readerRequest = CreateRequest(
            HttpMethod.Get,
            "/api/v1/governance-evidence/notifications",
            "test-reader",
            "reader",
            permission: null);

        using HttpResponseMessage anonymous = await client.SendAsync(
            anonymousRequest,
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);
        using HttpResponseMessage reader = await client.SendAsync(
            readerRequest,
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, reader.StatusCode);
    }

    /// <summary>
    /// Verifies that every evidence endpoint rejects a correlation identifier longer than the recorded maximum before
    /// querying the ledger or opening a notification stream.
    /// </summary>
    [Fact]
    public async Task GovernanceEvidence_OverlongCorrelationId_ReturnsBadRequest()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateHttpsClient();
        string overlong = new('c', 129);
        using HttpRequestMessage notificationsRequest = CreateRequest(
            HttpMethod.Get,
            $"/api/v1/governance-evidence/notifications?correlationId={overlong}",
            "test-admin",
            "administrator",
            permission: null);

        using HttpResponseMessage evidence = await GetEvidenceAsync(client, overlong);
        using HttpResponseMessage verification = await GetVerificationAsync(client, overlong);
        using HttpResponseMessage notifications = await client.SendAsync(
            notificationsRequest,
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);
        using HttpResponseMessage boundary = await GetEvidenceAsync(client, new string('c', 128));

        Assert.Equal(HttpStatusCode.BadRequest, evidence.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, verification.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notifications.StatusCode);
        Assert.NotEqual("text/event-stream", notifications.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, boundary.StatusCode);
    }

    /// <summary>
    /// Verifies that notifier failures cannot fail or delay the governed request or audit-completion delivery.
    /// </summary>
    [Fact]
    public async Task GovernanceEvidenceNotifications_ThrowingNotifier_DoesNotAffectGovernanceOrOutbox()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(
            configureServices: services =>
            {
                services.RemoveAll<IGovernanceEvidenceNotifier>();
                services.AddSingleton<IGovernanceEvidenceNotifier, ThrowingGovernanceEvidenceNotifier>();
            });
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostAsync(
            client,
            "records",
            role: "administrator",
            form: new Dictionary<string, string> { ["Title"] = "Notifier failure" });
        ApplicationAuditCompletionDispatchSummary summary = await DispatchAsync(factory);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, summary.DeliveredCount);
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await dbContext.GovernedOperationRecords.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await dbContext.AsiBackboneDecisionReceiptLifecycleEvents.CountAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies that every governed operation persists exactly one decision per request with its outcome, including
    /// denials and acknowledgment challenges.
    /// </summary>
    /// <param name="action">The governed action.</param>
    /// <param name="role">The caller role.</param>
    /// <param name="permission">The caller permission.</param>
    /// <param name="expectedStatus">The expected HTTP status.</param>
    /// <param name="expectedOutcome">The expected persisted decision outcome.</param>
    [Theory]
    [InlineData("administrator", "administrator", null, HttpStatusCode.OK, GovernanceDecisionOutcome.Allowed)]
    [InlineData("administrator", "reader", null, HttpStatusCode.Forbidden, GovernanceDecisionOutcome.Denied)]
    [InlineData("capability", null, ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope, HttpStatusCode.OK, GovernanceDecisionOutcome.Allowed)]
    [InlineData("capability", null, null, HttpStatusCode.Forbidden, GovernanceDecisionOutcome.Denied)]
    [InlineData("consequential", "administrator", ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope, HttpStatusCode.PreconditionRequired, GovernanceDecisionOutcome.AcknowledgmentRequired)]
    [InlineData("consequential", "administrator", null, HttpStatusCode.Forbidden, GovernanceDecisionOutcome.Denied)]
    public async Task GovernedOperation_PersistsOneDecisionWithOutcome(
        string action,
        string? role,
        string? permission,
        HttpStatusCode expectedStatus,
        GovernanceDecisionOutcome expectedOutcome)
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostAsync(client, action, role: role, permission: permission);

        Assert.Equal(expectedStatus, response.StatusCode);
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuditLedgerRecordEntity decision = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .AsiBackboneAuditLedgerRecords
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expectedOutcome.ToString(), decision.Outcome);
        Assert.Equal("test-user", decision.ActorId);
    }

    /// <summary>
    /// Verifies that requests stopped before governance (anonymous callers) and ungoverned reads persist no decisions.
    /// </summary>
    [Fact]
    public async Task UngovernedAndUnauthenticatedRequests_PersistNoDecision()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage description = await client.GetAsync(_basePath, TestContext.Current.CancellationToken);
        using HttpRequestMessage anonymousRequest = new(HttpMethod.Post, $"{_basePath}/records")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["Title"] = "Anonymous" })
        };
        using HttpResponseMessage anonymous = await client.SendAsync(anonymousRequest, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, description.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(0, await dbContext.AsiBackboneAuditLedgerRecords.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await dbContext.GovernedOperationRecords.CountAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies that an invalid request is rejected after governance allowed it, leaving the decision but no change.
    /// </summary>
    /// <param name="title">The invalid title.</param>
    [Theory]
    [InlineData("")]
    [InlineData("This title is far too long for the record because it repeats itself over and over and over and over and over and over and over and over and over and over and over and over and over and over and over and over again")]
    public async Task CreateRecord_InvalidTitle_IsRejectedWithoutMutation(string title)
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostAsync(
            client,
            "records",
            role: "administrator",
            form: new Dictionary<string, string> { ["Title"] = title });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(0, await dbContext.GovernedOperationRecords.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await dbContext.ApplicationAuditCompletionOutboxEntries.CountAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies that a governed operation fails closed, without executing, when its decision cannot be persisted.
    /// </summary>
    [Fact]
    public async Task GovernedOperation_EvidenceCannotBePersisted_FailsClosed()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(migrate: false);
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostAsync(client, "administrator", role: "administrator");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(
            "Administrator-governed action executed.",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _database.Dispose();
    }

    private static async Task<ApplicationAuditCompletionDispatchSummary> DispatchAsync(WebApplicationFactory<Program> factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IApplicationAuditCompletionOutboxDispatcher>()
            .DispatchReadyAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string action,
        string? role = null,
        string? permission = null,
        Dictionary<string, string>? form = null)
    {
        string user = role == "administrator" && action == "records" ? "test-admin" : "test-user";
        using HttpRequestMessage tokenRequest = CreateRequest(HttpMethod.Get, "/test/authentication/antiforgery-token", user, role, permission);
        using HttpResponseMessage tokenResponse = await client.SendAsync(tokenRequest, TestContext.Current.CancellationToken);
        _ = tokenResponse.EnsureSuccessStatusCode();

        Dictionary<string, string> values = form is null ? [] : new Dictionary<string, string>(form);
        values[_antiforgeryFormFieldName] = await tokenResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, $"{_basePath}/{action}", user, role, permission);
        request.Headers.Accept.ParseAdd("application/json");
        request.Content = new FormUrlEncodedContent(values);

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string user, string? role, string? permission)
    {
        HttpRequestMessage request = new(method, path);
        request.Headers.Add(_testUserHeaderName, user);

        if (role is not null)
        {
            request.Headers.Add(_testRoleHeaderName, role);
        }

        if (permission is not null)
        {
            request.Headers.Add(_testPermissionHeaderName, permission);
        }

        return request;
    }

    private static async Task<HttpResponseMessage> GetEvidenceAsync(
        HttpClient client,
        string correlationId,
        string? user = "test-admin",
        string? role = "administrator")
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            $"/api/v1/governance-evidence/{Uri.EscapeDataString(correlationId)}");
        request.Headers.Accept.ParseAdd("application/json");

        if (user is not null)
        {
            request.Headers.Add(_testUserHeaderName, user);
        }

        if (role is not null)
        {
            request.Headers.Add(_testRoleHeaderName, role);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> GetVerificationAsync(
        HttpClient client,
        string correlationId,
        string? user = "test-admin",
        string? role = "administrator")
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            $"/api/v1/governance-evidence/{Uri.EscapeDataString(correlationId)}/verification");
        request.Headers.Accept.ParseAdd("application/json");

        if (user is not null)
        {
            request.Headers.Add(_testUserHeaderName, user);
        }

        if (role is not null)
        {
            request.Headers.Add(_testRoleHeaderName, role);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<GovernanceEvidenceRecordVerification> GetSingleVerificationAsync(
        HttpClient client,
        string correlationId)
    {
        using HttpResponseMessage response = await GetVerificationAsync(client, correlationId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GovernanceEvidenceVerificationResponse verification = Assert.IsType<GovernanceEvidenceVerificationResponse>(
            await response.Content.ReadFromJsonAsync<GovernanceEvidenceVerificationResponse>(
                TestContext.Current.CancellationToken));

        return Assert.Single(verification.Records);
    }

    private Task<WebApplicationFactory<Program>> CreateFactoryAsync(
        bool migrate = true,
        Action<IServiceCollection>? configureServices = null,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        Dictionary<string, string?> configuration = new(StringComparer.Ordinal)
        {
            ["ReferenceApplication:Authentication:Enabled"] = "true",
            ["ReferenceApplication:Authentication:DefaultScheme"] = _testAuthenticationScheme,
            ["ReferenceApplication:Authentication:DefaultChallengeScheme"] = _testAuthenticationScheme,
            ["ReferenceApplication:Authentication:DefaultSignInScheme"] = _testAuthenticationScheme,
            ["ReferenceApplication:Authentication:Cookie:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Cookie:Scheme"] = "Cookies",
            ["ReferenceApplication:Authorization:RoleClaimType"] = ApplicationClaimTypes.Role,
            ["ReferenceApplication:Authorization:PermissionClaimType"] = ApplicationClaimTypes.Permission,
            ["ReferenceApplication:Authorization:AdministratorRoles:0"] = "administrator"
        };

        foreach (KeyValuePair<string, string?> setting in settings ?? new Dictionary<string, string?>())
        {
            configuration[setting.Key] = setting.Value;
        }

        return _database.ApplyAsync(
            ApplicationWebApplicationFactory.CreateAllowingAnonymousAccess(configuration)
            .WithWebHostBuilder(builder =>
            {
                // Settings the host reads while registering services, such as the OpenTelemetry switches, must be
                // host settings: the factory's in-memory configuration is applied only after registration.
                foreach (KeyValuePair<string, string?> setting in settings ?? new Dictionary<string, string?>())
                {
                    _ = builder.UseSetting(setting.Key, setting.Value);
                }

                _ = builder.ConfigureServices(services =>
                {
                    _ = services
                        .AddAuthentication()
                        .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                            _testAuthenticationScheme,
                            _ => { });
                    configureServices?.Invoke(services);
                });
            }),
            migrate);
    }

    private static async Task<IReadOnlyList<GovernanceEvidenceNotification>> ReadNotificationsAsync(
        IGovernanceEvidenceSubscription subscription,
        int count,
        CancellationToken cancellationToken)
    {
        var notifications = new List<GovernanceEvidenceNotification>(count);
        await foreach (GovernanceEvidenceNotification notification in subscription
            .ReadAllAsync(cancellationToken))
        {
            notifications.Add(notification);
            if (notifications.Count == count)
            {
                break;
            }
        }

        return notifications;
    }

    private sealed class ThrowingGovernanceEvidenceNotifier : IGovernanceEvidenceNotifier
    {
        public void Publish(GovernanceEvidenceNotification notification)
        {
            throw new InvalidOperationException("Notification transport failed.");
        }

        public IGovernanceEvidenceSubscription Subscribe()
        {
            throw new InvalidOperationException("Notification transport failed.");
        }
    }

    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        /// <inheritdoc />
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(_testUserHeaderName, out StringValues user) || string.IsNullOrWhiteSpace(user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            List<Claim> claims = [new Claim("sub", user.ToString())];
            AddClaimFromHeader(claims, _testRoleHeaderName, ApplicationClaimTypes.Role);
            AddClaimFromHeader(claims, _testPermissionHeaderName, ApplicationClaimTypes.Permission);

            ClaimsPrincipal principal = new(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }

        private void AddClaimFromHeader(List<Claim> claims, string headerName, string claimType)
        {
            if (Request.Headers.TryGetValue(headerName, out StringValues value) && !string.IsNullOrWhiteSpace(value))
            {
                claims.Add(new Claim(claimType, value.ToString()));
            }
        }
    }
}
