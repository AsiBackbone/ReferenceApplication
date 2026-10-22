using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using AsiBackbone.EntityFrameworkCore.Persistence;
using AsiBackbone.OpenTelemetry;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ReferenceApplication.Infrastructure.Data;
using ReferenceApplication.Infrastructure.Data.Auditing;
using ReferenceApplication.Web.Authentication.Claims;
using ReferenceApplication.Web.Extensions;
using ReferenceApplication.Web.Governance;
using ReferenceApplication.Web.Governance.Pipeline;
using ReferenceApplication.Web.HealthChecks;
using ReferenceApplication.Web.Services;
using ReferenceApplication.Web.Tests.Extensions;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies the Development-only pipeline inspector: the outbox entry of a governed record moving to delivered with its
/// lifecycle event, a failed delivery's retry schedule, ordered stage timing, reconciliation findings, in-process
/// telemetry, that snapshots write nothing, and that the inspector is reachable only by administrators in Development.
/// </summary>
public sealed class PipelineInspectorTests : IDisposable
{
    private const string _authenticationScheme = "PipelineInspectorTestAuthentication";
    private const string _userHeader = "X-Test-User";
    private const string _roleHeader = "X-Test-Role";
    private const string _user = "inspector-user";
    private const string _administratorRole = "administrator";
    private const string _antiforgeryField = "__RequestVerificationToken";
    private const string _outboxSection = "ReferenceApplication:AuditCompletionOutbox";

    private static readonly string[] _stageOrder =
    [
        "request-received",
        "decision-evaluated",
        "receipt-appended",
        "mutation-saved",
        "outbox-staged",
        "response-sent",
        "outbox-dispatched",
        "lifecycle-appended"
    ];

    private readonly TestSqliteDatabase _database = TestSqliteDatabase.Create();

    /// <summary>
    /// Verifies that after a governed record is created, its outbox entry is shown as pending, then, once the outbox
    /// dispatches it, as delivered with the lifecycle event the delivery appended.
    /// </summary>
    [Fact]
    public async Task Snapshot_ShowsTheRecordsOutboxEntryMovingToDelivered_WithItsLifecycleEvent()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateHttpsClient();
        string correlationId = await CreateRecordAsync(client, factory);

        using JsonDocument pending = await GetSnapshotAsync(client, correlationId);
        JsonElement pendingEntry = FindEntry(pending.RootElement, correlationId);
        Assert.Equal(ApplicationAuditCompletionOutboxStatuses.Pending, pendingEntry.GetProperty("status").GetString());
        Assert.False(pendingEntry.GetProperty("lifecycleEventAppended").GetBoolean());
        Assert.Equal(PipelineStageStatuses.Pending, FindStage(pending.RootElement, "outbox-dispatched").GetProperty("status").GetString());

        ApplicationAuditCompletionDispatchSummary dispatched = await DispatchAsync(factory);
        Assert.Equal(1, dispatched.DeliveredCount);

        using JsonDocument delivered = await GetSnapshotAsync(client, correlationId);
        JsonElement deliveredEntry = FindEntry(delivered.RootElement, correlationId);
        Assert.Equal(ApplicationAuditCompletionOutboxStatuses.Delivered, deliveredEntry.GetProperty("status").GetString());
        Assert.True(deliveredEntry.GetProperty("lifecycleEventAppended").GetBoolean());
        Assert.Equal(JsonValueKind.String, deliveredEntry.GetProperty("deliveredUtc").ValueKind);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        DecisionReceiptLifecycleEventEntity lifecycleEvent = await scope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>()
            .AsiBackboneDecisionReceiptLifecycleEvents
            .AsNoTracking()
            .SingleAsync(entry => entry.CorrelationId == correlationId, TestContext.Current.CancellationToken);
        Assert.Equal(lifecycleEvent.EventId, deliveredEntry.GetProperty("lifecycleEventId").GetString());
        Assert.Equal(PipelineStageStatuses.Recorded, FindStage(delivered.RootElement, "lifecycle-appended").GetProperty("status").GetString());
    }

    /// <summary>
    /// Verifies that a failed delivery is shown with its attempt count, last error, and next attempt, and that the
    /// outbox status filter selects it.
    /// </summary>
    [Fact]
    public async Task Snapshot_ShowsAFailedDeliveryWithItsAttemptCountAndRetrySchedule()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(failingDelivery: true);
        using HttpClient client = factory.CreateHttpsClient();
        string correlationId = await CreateRecordAsync(client, factory);

        ApplicationAuditCompletionDispatchSummary dispatched = await DispatchAsync(factory);
        Assert.Equal(1, dispatched.RetryScheduledCount);

        using JsonDocument snapshot = await GetSnapshotAsync(
            client,
            correlationId,
            status: ApplicationAuditCompletionOutboxStatuses.RetryableFailure);
        JsonElement outbox = snapshot.RootElement.GetProperty("outbox");
        JsonElement entry = FindEntry(snapshot.RootElement, correlationId);
        JsonElement stage = FindStage(snapshot.RootElement, "outbox-dispatched");

        Assert.Equal(ApplicationAuditCompletionOutboxStatuses.RetryableFailure, outbox.GetProperty("statusFilter").GetString());
        Assert.Equal(1, outbox.GetProperty("totalRetryCount").GetInt64());
        Assert.Equal(ApplicationAuditCompletionOutboxStatuses.RetryableFailure, entry.GetProperty("status").GetString());
        Assert.Equal(1, entry.GetProperty("retryCount").GetInt32());
        Assert.Equal(FailingCompletionPublisher.ErrorCode, entry.GetProperty("lastErrorCode").GetString());
        Assert.True(
            entry.GetProperty("nextAttemptUtc").GetDateTimeOffset() > entry.GetProperty("lastAttemptUtc").GetDateTimeOffset());
        Assert.Equal(PipelineStageStatuses.Retrying, stage.GetProperty("status").GetString());
        Assert.Contains(
            $"Attempt 1 of {outbox.GetProperty("maxRetryAttempts").GetInt32()}",
            stage.GetProperty("note").GetString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that one request's stages come in pipeline order with the synchronous stages first, and that the
    /// persisted timestamps are in that order.
    /// </summary>
    [Fact]
    public async Task Stages_AreInPipelineOrder_WithTheSynchronousBoundaryLabelled()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateHttpsClient();
        string correlationId = await CreateRecordAsync(client, factory);
        _ = await DispatchAsync(factory);

        using JsonDocument snapshot = await GetSnapshotAsync(client, correlationId);
        JsonElement[] stages = [.. snapshot.RootElement.GetProperty("request").GetProperty("stages").EnumerateArray()];

        Assert.Equal(_stageOrder, stages.Select(static stage => stage.GetProperty("key").GetString()));
        bool[] synchronous = [.. stages.Select(static stage => stage.GetProperty("synchronous").GetBoolean())];
        int boundary = Array.IndexOf(synchronous, false);
        Assert.Equal(_stageOrder.Length - 2, boundary);
        Assert.DoesNotContain(false, synchronous[..boundary]);
        Assert.DoesNotContain(true, synchronous[boundary..]);

        // Application timestamps are stored to the millisecond, so the persisted stages are compared at that precision.
        // The server span's times are left out: the span can end after the client has its response.
        string[] persisted = ["decision-evaluated", "receipt-appended", "mutation-saved", "outbox-staged", "outbox-dispatched", "lifecycle-appended"];
        long[] milliseconds = [.. stages
            .Where(stage => persisted.Contains(stage.GetProperty("key").GetString()))
            .Select(static stage =>
            {
                Assert.Equal(PipelineStageStatuses.Recorded, stage.GetProperty("status").GetString());
                return stage.GetProperty("occurredUtc").GetDateTimeOffset().ToUnixTimeMilliseconds();
            })];
        Assert.Equal(persisted.Length, milliseconds.Length);
        long[] ordered = [.. milliseconds.Order()];
        Assert.Equal(ordered, milliseconds);
    }

    /// <summary>
    /// Verifies that the inspector reads the host reconciliation summary and open findings: a mutation whose audit
    /// completion was never staged is a missing completion.
    /// </summary>
    [Fact]
    public async Task Snapshot_ShowsReconciliationSummaryAndOpenFindings()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(outboxEnabled: false);
        using HttpClient client = factory.CreateHttpsClient();
        _ = await CreateRecordAsync(client, factory);

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ApplicationAuditReconciliationSummary summary = await scope.ServiceProvider
                .GetRequiredService<IApplicationAuditReconciler>()
                .ReconcileAsync(TestContext.Current.CancellationToken);
            Assert.True(summary.MissingCompletionCount > 0);
        }

        using JsonDocument snapshot = await GetSnapshotAsync(client, correlationId: null);
        JsonElement reconciliation = snapshot.RootElement.GetProperty("reconciliation");

        Assert.True(reconciliation.GetProperty("enabled").GetBoolean());
        Assert.False(reconciliation.GetProperty("workerEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.String, reconciliation.GetProperty("lastRunUtc").ValueKind);
        Assert.True(reconciliation.GetProperty("openFindingCount").GetInt64() > 0);
        Assert.Contains(
            reconciliation.GetProperty("openFindings").EnumerateArray(),
            static finding => finding.GetProperty("reasonCode").GetString() ==
                ApplicationAuditReconciliationReasonCodes.MissingCompletion);
        Assert.False(snapshot.RootElement.GetProperty("outbox").GetProperty("enabled").GetBoolean());
    }

    /// <summary>
    /// Verifies that reconciliation reports missing completion evidence even when the Development pipeline inspector
    /// is not registered.
    /// </summary>
    [Fact]
    public async Task Reconciliation_WithoutPipelineInspector_ReportsMissingCompletion()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(
            outboxEnabled: false,
            pipelineInspectorEnabled: false);
        using HttpClient client = factory.CreateHttpsClient();
        _ = await CreateRecordAsync(client, factory);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        Assert.Null(scope.ServiceProvider.GetService<IPipelineInspector>());

        IApplicationAuditReconciler reconciler = scope.ServiceProvider
            .GetRequiredService<IApplicationAuditReconciler>();
        ApplicationAuditReconciliationSummary summary = await reconciler
            .ReconcileAsync(TestContext.Current.CancellationToken);
        Assert.True(summary.MissingCompletionCount > 0);

        IReadOnlyList<ApplicationAuditReconciliationFindingItem> findings = await reconciler.QueryFindingsAsync(
            new ApplicationAuditReconciliationQuery(
                ReasonCode: ApplicationAuditReconciliationReasonCodes.MissingCompletion),
            TestContext.Current.CancellationToken);
        Assert.Contains(
            findings,
            static finding => finding.ReasonCode == ApplicationAuditReconciliationReasonCodes.MissingCompletion);
    }

    /// <summary>
    /// Verifies that the in-process listener lists the sources and meters present, marks which the host's
    /// OpenTelemetry configuration collects, including the governance source and meters, reads the host's own gauges,
    /// and shows the request's server span and its governance span.
    /// </summary>
    [Fact]
    public async Task Telemetry_ListsSourcesAndMeters_AndShowsTheRequestsServerSpan()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateHttpsClient();
        string correlationId = await CreateRecordAsync(client, factory);

        // The server span ends as the request finishes, which can be just after the client has the response; the
        // governance span ends earlier, inside the request, so wait for the server span itself.
        JsonElement? request = null;
        JsonElement telemetry = default;
        JsonElement candidate = default;
        for (int attempt = 0; attempt < 25 && request is null; attempt++)
        {
            using JsonDocument snapshot = await GetSnapshotAsync(client, correlationId);
            telemetry = snapshot.RootElement.GetProperty("telemetry").Clone();
            candidate = snapshot.RootElement.GetProperty("request").Clone();
            if (candidate.GetProperty("spans").EnumerateArray().Any(static span =>
                span.GetProperty("kind").GetString() == "Server"))
            {
                request = candidate;
            }
            else
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
            }
        }

        if (request is null)
        {
            // Report what the inspector saw, so a failure says whether the trace or the listener is at fault.
            string sources = string.Join(", ", telemetry.GetProperty("activitySources").EnumerateArray().Select(static source =>
                $"{source.GetProperty("name").GetString()}={source.GetProperty("spansObserved").GetRawText()}"));
            Assert.Fail(
                $"No span for the request. found={candidate.GetProperty("found").GetBoolean()}; " +
                $"traceId={candidate.GetProperty("traceId").GetRawText()}; tracing={telemetry.GetProperty("tracingEnabled").GetRawText()}; " +
                $"request-received={FindStage(candidate, "request-received").GetProperty("status").GetString()}; " +
                $"sources=[{sources}]");
        }

        JsonElement observed = Assert.NotNull(request);
        JsonElement span = observed.GetProperty("spans").EnumerateArray().Single(static span =>
            span.GetProperty("kind").GetString() == "Server");
        Assert.Equal("Microsoft.AspNetCore", span.GetProperty("source").GetString());
        Assert.Contains(
            span.GetProperty("tags").EnumerateObject(),
            static tag => tag.Value.GetString()!.Contains("governed-operations/records", StringComparison.Ordinal));
        Assert.Equal(PipelineStageStatuses.Recorded, FindStage(observed, "request-received").GetProperty("status").GetString());

        // AsiBackbone 7.5.0 records the request's trace on the policy-evaluated decision, which is what links the spans.
        string traceId = span.GetProperty("traceId").GetString()!;
        Assert.Equal(traceId, observed.GetProperty("traceId").GetString());
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            AuditLedgerRecordEntity decision = await scope.ServiceProvider
                .GetRequiredService<ApplicationDbContext>()
                .AsiBackboneAuditLedgerRecords
                .AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken);
            Assert.Contains(traceId, decision.TraceId, StringComparison.OrdinalIgnoreCase);
        }

        JsonElement aspNetCore = telemetry.GetProperty("activitySources").EnumerateArray()
            .Single(static source => source.GetProperty("name").GetString() == "Microsoft.AspNetCore");
        Assert.True(aspNetCore.GetProperty("addedToOpenTelemetry").GetBoolean());
        Assert.True(aspNetCore.GetProperty("spansObserved").GetInt64() > 0);

        // Governance instrumentation adds AsiBackbone's governance source and meter and the reconciliation gauges.
        JsonElement governanceSource = telemetry.GetProperty("activitySources").EnumerateArray()
            .Single(static source => source.GetProperty("name").GetString() == OpenTelemetryGovernanceInstrumentation.ActivitySourceName);
        Assert.True(governanceSource.GetProperty("addedToOpenTelemetry").GetBoolean());
        Assert.True(governanceSource.GetProperty("spansObserved").GetInt64() > 0);
        Assert.Contains(
            observed.GetProperty("spans").EnumerateArray(),
            static span => span.GetProperty("source").GetString() == OpenTelemetryGovernanceInstrumentation.ActivitySourceName);

        JsonElement governanceMeter = telemetry.GetProperty("meters").EnumerateArray()
            .Single(static meter => meter.GetProperty("name").GetString() == OpenTelemetryGovernanceInstrumentation.MeterName);
        Assert.True(governanceMeter.GetProperty("addedToOpenTelemetry").GetBoolean());

        JsonElement reconciliationMeter = telemetry.GetProperty("meters").EnumerateArray()
            .Single(static meter => meter.GetProperty("name").GetString() == PipelineTelemetryRecorder.ReconciliationMeterName);
        Assert.True(reconciliationMeter.GetProperty("addedToOpenTelemetry").GetBoolean());
        Assert.Contains(
            telemetry.GetProperty("hostGauges").EnumerateArray(),
            static gauge => gauge.GetProperty("instrument").GetString() == "ncat.audit.outbox.pending");
    }

    /// <summary>
    /// Verifies that snapshots, with and without a request, leave every outbox entry and every evidence row unchanged.
    /// </summary>
    [Fact]
    public async Task Snapshot_WritesNothing()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(failingDelivery: true);
        using HttpClient client = factory.CreateHttpsClient();
        string correlationId = await CreateRecordAsync(client, factory);
        _ = await DispatchAsync(factory);

        string before = await DescribeStoredStateAsync(factory);
        for (int read = 0; read < 3; read++)
        {
            using JsonDocument withRequest = await GetSnapshotAsync(client, correlationId);
            using JsonDocument withoutRequest = await GetSnapshotAsync(client, correlationId: null);
            Assert.Equal(JsonValueKind.Object, withRequest.RootElement.GetProperty("request").ValueKind);
            Assert.Equal(JsonValueKind.Null, withoutRequest.RootElement.GetProperty("request").ValueKind);
        }

        Assert.Equal(before, await DescribeStoredStateAsync(factory));
    }

    /// <summary>
    /// Verifies input handling: an unknown correlation identifier is reported as not found, and an unknown status or
    /// an overlong correlation identifier is rejected.
    /// </summary>
    [Fact]
    public async Task Snapshot_HandlesUnknownAndInvalidInputs()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateHttpsClient();

        using JsonDocument unknown = await GetSnapshotAsync(client, "not-a-correlation");
        using HttpResponseMessage badStatus = await GetAsync(
            client,
            $"{PipelineInspectorServiceExtensions.SnapshotPath}?status=Unknown",
            _administratorRole);
        using HttpResponseMessage tooLong = await GetAsync(
            client,
            $"{PipelineInspectorServiceExtensions.SnapshotPath}?correlationId={new string('c', PipelineInspectorOptions.MaximumCorrelationIdLength + 1)}",
            _administratorRole);

        Assert.False(unknown.RootElement.GetProperty("request").GetProperty("found").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    /// <summary>
    /// Verifies that a non-administrator cannot read the pipeline.
    /// </summary>
    [Fact]
    public async Task Snapshot_NonAdministrator_IsForbidden()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await GetAsync(client, PipelineInspectorServiceExtensions.SnapshotPath, "reader");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Verifies that outside Development the inspector and its route stay absent while host reconciliation, its worker,
    /// and its audit-integrity health check remain registered.
    /// </summary>
    /// <param name="environmentName">The host environment.</param>
    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Feature_OutsideDevelopment_LeavesInspectorAbsentButRegistersReconciliation(string environmentName)
    {
        using WebApplicationFactory<Program> factory = ApplicationWebApplicationFactory
            .CreateAllowingAnonymousAccess(new Dictionary<string, string?>
            {
                [$"{PipelineInspectorOptions.SectionName}:Enabled"] = "true",
                ["ReferenceApplication:AuditCompletionOutbox:Enabled"] = "false",
                [$"{GovernanceEvidenceServiceExtensions.AuditReconciliationSectionName}:Enabled"] = "false",
                [$"{GovernanceEvidenceServiceExtensions.AuditReconciliationSectionName}:RunWorker"] = "false"
            }, environmentName)
            .WithWebHostBuilder(static builder => builder.UseSetting($"{PipelineInspectorOptions.SectionName}:Enabled", "true"));

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        Assert.Null(scope.ServiceProvider.GetService<IPipelineInspector>());
        Assert.Null(scope.ServiceProvider.GetService<PipelineTelemetryRecorder>());
        Assert.NotNull(scope.ServiceProvider.GetService<IApplicationAuditReconciler>());
        Assert.Contains(
            factory.Services.GetServices<IHostedService>(),
            static service => service is ApplicationAuditReconciliationHostedService);

        HealthCheckServiceOptions healthCheckOptions = factory.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value;
        Assert.Contains(
            healthCheckOptions.Registrations,
            static registration => registration.Tags.Contains(ApplicationHealthCheckTags.Audit));

        using HttpClient client = factory.CreateHttpsClient();
        using HttpResponseMessage inspectorResponse = await client.GetAsync(
            PipelineInspectorServiceExtensions.SnapshotPath,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, inspectorResponse.StatusCode);

        using HttpResponseMessage healthResponse = await client.GetAsync(
            "/health/audit-integrity",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
        Assert.Equal(
            "Healthy",
            await healthResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies that the explorer shows the Pipeline inspector tab and output only to an administrator in Development.
    /// </summary>
    [Fact]
    public async Task Explorer_ShowsInspectorOnlyToAdministrators()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = factory.CreateHttpsClient();

        string administratorPage = await GetExplorerAsync(client, _administratorRole);
        string readerPage = await GetExplorerAsync(client, "reader");

        Assert.Contains("data-pipeline-inspector=\"true\"", administratorPage, StringComparison.Ordinal);
        Assert.Contains("aria-controls=\"explorer-panel-pipeline\"", administratorPage, StringComparison.Ordinal);
        Assert.Contains("data-output-view=\"pipeline\"", administratorPage, StringComparison.Ordinal);
        Assert.Contains("Demonstration, not a benchmark", administratorPage, StringComparison.Ordinal);
        Assert.Contains("benchmarks/governance-overhead", administratorPage, StringComparison.Ordinal);
        Assert.Contains(
            $"<option value=\"{ApplicationAuditCompletionOutboxStatuses.RetryableFailure}\">",
            administratorPage,
            StringComparison.Ordinal);
        Assert.Contains("data-pipeline-inspector=\"false\"", readerPage, StringComparison.Ordinal);
        Assert.DoesNotContain("explorer-panel-pipeline", readerPage, StringComparison.Ordinal);
        Assert.DoesNotContain("data-output-view=\"pipeline\"", readerPage, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _database.Dispose();
    }

    private Task<WebApplicationFactory<Program>> CreateFactoryAsync(
        bool outboxEnabled = true,
        bool failingDelivery = false,
        bool pipelineInspectorEnabled = true)
    {
        Dictionary<string, string?> configuration = new()
        {
            ["ReferenceApplication:Authentication:Enabled"] = "true",
            ["ReferenceApplication:Authentication:DefaultScheme"] = _authenticationScheme,
            ["ReferenceApplication:Authentication:DefaultChallengeScheme"] = _authenticationScheme,
            ["ReferenceApplication:Authentication:DefaultSignInScheme"] = _authenticationScheme,
            ["ReferenceApplication:Authentication:Cookie:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Cookie:Scheme"] = "Cookies",
            ["ReferenceApplication:Authorization:RoleClaimType"] = ApplicationClaimTypes.Role,
            ["ReferenceApplication:Authorization:AdministratorRoles:0"] = _administratorRole,

            // The tests dispatch the outbox and run reconciliation themselves, so the background workers are held off:
            // the outbox worker polls once at startup and then waits an hour, and the reconciliation worker never runs.
            [$"{_outboxSection}:Enabled"] = outboxEnabled ? "true" : "false",
            [$"{_outboxSection}:PollInterval"] = "01:00:00",
            [$"{_outboxSection}:MaximumCycleRetryDelay"] = "01:00:00",
            [$"{GovernanceEvidenceServiceExtensions.AuditReconciliationSectionName}:RunWorker"] = "false",
            [$"{GovernanceEvidenceServiceExtensions.AuditReconciliationSectionName}:CompletionGracePeriod"] = "00:00:00"
        };

        // Feature switches are read during service registration, before test configuration sources are applied, so
        // they are pinned as host settings. That also keeps local appsettings.Development.json values and user secrets
        // from changing what these tests exercise.
        return _database.ApplyAsync(ApplicationWebApplicationFactory
            .CreateAllowingAnonymousAccess(configuration, Environments.Development)
            .WithWebHostBuilder(builder => builder
                .UseSetting(
                    $"{PipelineInspectorOptions.SectionName}:Enabled",
                    pipelineInspectorEnabled ? "true" : "false")
                .UseSetting("ReferenceApplication:DecisionReplay:Enabled", "false")
                .UseSetting("ReferenceApplication:PolicySimulator:Enabled", "false")
                .UseSetting("ReferenceApplication:DemoIdentity:Enabled", "false")
                .UseSetting("ReferenceApplication:GovernanceLoadDemonstration:Enabled", "false")
                .UseSetting("ReferenceApplication:CapabilityTokens:Enabled", "false")
                .ConfigureServices(static services => services
                    .AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                        _authenticationScheme,
                        static _ => { }))
                .ConfigureTestServices(services =>
                {
                    if (failingDelivery)
                    {
                        services.RemoveAll<IApplicationAuditCompletionPublisher>();
                        services.AddScoped<IApplicationAuditCompletionPublisher, FailingCompletionPublisher>();
                    }
                })));
    }

    private static async Task<string> CreateRecordAsync(HttpClient client, WebApplicationFactory<Program> factory)
    {
        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Post,
            "/api/v1/governed-operations/records",
            _administratorRole);
        request.Headers.Accept.ParseAdd("application/json");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            [_antiforgeryField] = await GetAntiforgeryTokenAsync(client),
            ["Title"] = "Pipeline inspector record"
        });
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuditLedgerRecordEntity decision = await scope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>()
            .AsiBackboneAuditLedgerRecords
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        return decision.CorrelationId!;
    }

    private static async Task<ApplicationAuditCompletionDispatchSummary> DispatchAsync(WebApplicationFactory<Program> factory)
    {
        // The test stands in for the background worker: this is the outbox's own delivery path.
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IApplicationAuditCompletionOutboxDispatcher>()
            .DispatchReadyAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<JsonDocument> GetSnapshotAsync(HttpClient client, string? correlationId, string? status = null)
    {
        List<string> query = [];
        if (correlationId is not null)
        {
            query.Add($"correlationId={Uri.EscapeDataString(correlationId)}");
        }

        if (status is not null)
        {
            query.Add($"status={Uri.EscapeDataString(status)}");
        }

        string path = query.Count == 0
            ? PipelineInspectorServiceExtensions.SnapshotPath
            : $"{PipelineInspectorServiceExtensions.SnapshotPath}?{string.Join('&', query)}";
        using HttpResponseMessage response = await GetAsync(client, path, _administratorRole);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"The snapshot returned {(int)response.StatusCode}: {body}");
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        return JsonDocument.Parse(body);
    }

    private static JsonElement FindEntry(JsonElement snapshot, string correlationId)
    {
        return snapshot.GetProperty("outbox").GetProperty("entries").EnumerateArray()
            .Single(entry => entry.GetProperty("correlationId").GetString() == correlationId);
    }

    private static JsonElement FindStage(JsonElement snapshotOrRequest, string key)
    {
        JsonElement request = snapshotOrRequest.TryGetProperty("stages", out _)
            ? snapshotOrRequest
            : snapshotOrRequest.GetProperty("request");
        return request.GetProperty("stages").EnumerateArray()
            .Single(stage => stage.GetProperty("key").GetString() == key);
    }

    private static async Task<string> DescribeStoredStateAsync(WebApplicationFactory<Program> factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        var entries = await dbContext.ApplicationAuditCompletionOutboxEntries
            .AsNoTracking()
            .OrderBy(static entry => entry.Id)
            .Select(static entry => new
            {
                entry.Id,
                entry.Status,
                entry.RetryCount,
                entry.LastAttemptUtc,
                entry.NextAttemptUtc,
                entry.DeliveredUtc,
                entry.ConcurrencyStamp
            })
            .ToListAsync(cancellationToken);

        return JsonSerializer.Serialize(new
        {
            Entries = entries,
            Ledger = await dbContext.AsiBackboneAuditLedgerRecords.CountAsync(cancellationToken),
            Lifecycle = await dbContext.AsiBackboneDecisionReceiptLifecycleEvents.CountAsync(cancellationToken),
            MutationAudit = await dbContext.AuditRecords.CountAsync(cancellationToken),
            Findings = await dbContext.ApplicationAuditReconciliationFindings.CountAsync(cancellationToken)
        });
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string role)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, path, role);
        request.Headers.Accept.ParseAdd("application/json");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string> GetExplorerAsync(HttpClient client, string role)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, "/governance-explorer", role);
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Get,
            "/test/authentication/antiforgery-token",
            _administratorRole);
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string role)
    {
        HttpRequestMessage request = new(method, path);
        request.Headers.Add(_userHeader, _user);
        request.Headers.Add(_roleHeader, role);
        return request;
    }

    private sealed class FailingCompletionPublisher : IApplicationAuditCompletionPublisher
    {
        public const string ErrorCode = "test-delivery-failure";

        public string Destination => AsiBackboneAuditCompletionPublisher.DestinationName;

        public ValueTask<ApplicationAuditCompletionPublishResult> PublishAsync(
            ApplicationAuditCompletionMessage message,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(ApplicationAuditCompletionPublishResult.Retry(
                ErrorCode,
                "The test destination is unavailable."));
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
            if (!Request.Headers.TryGetValue(_userHeader, out StringValues user) || string.IsNullOrWhiteSpace(user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            List<Claim> claims =
            [
                new(ApplicationClaimTypes.Subject, user.ToString()),
                new(ApplicationClaimTypes.Name, "Pipeline inspector test actor")
            ];
            if (Request.Headers.TryGetValue(_roleHeader, out StringValues role) && !string.IsNullOrWhiteSpace(role))
            {
                claims.Add(new Claim(ApplicationClaimTypes.Role, role.ToString()));
            }

            ClaimsPrincipal principal = new(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
