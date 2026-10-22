using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AsiBackbone.AspNetCore.CapabilityGrants;
using AsiBackbone.Core.Constraints;
using AsiBackbone.Core.Evaluation;
using AsiBackbone.EntityFrameworkCore.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReferenceApplication.Infrastructure.Data;
using ReferenceApplication.Infrastructure.Data.Auditing;
using ReferenceApplication.Web.Authentication.Demo;
using ReferenceApplication.Web.Extensions;
using ReferenceApplication.Web.Governance;
using ReferenceApplication.Web.Governance.FailureScenarios;
using ReferenceApplication.Web.Tests.Extensions;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies the Development-only failure scenarios: each one's HTTP outcome, reason code, and evidence shape, that the
/// governed action body runs only where a scenario expects it to, that every run disarms after one request, and that
/// the scenarios and their fault-injection seams are absent outside an active Development host.
/// </summary>
public sealed class FailureScenarioTests : IDisposable
{
    private const string _antiforgeryField = "__RequestVerificationToken";
    private const string _outboxSection = "ReferenceApplication:AuditCompletionOutbox";
    private const string _scenariosEnabledKey = $"{FailureScenarioOptions.SectionName}:Enabled";
    private const string _tokensSection = CapabilityTokenOptions.SectionName;

    // Generated for the test run, so no signing key is checked in.
    private static readonly Lazy<(string PrivateKeyPem, string PublicKeyPem)> _signingKey = new(static () =>
    {
        using var rsa = RSA.Create(2048);
        return (rsa.ExportPkcs8PrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    });

    private readonly TestSqliteDatabase _database = TestSqliteDatabase.Create();

    /// <summary>
    /// Verifies that the capability endpoint, called without the grant, is denied with the grant-required reason code
    /// and a recorded denial, and that the action does not run, even for an actor who otherwise holds the capability.
    /// </summary>
    [Fact]
    public async Task MissingCapabilityGrant_IsDeniedAndRecorded_AndTheActionDoesNotRun()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = await SignInAsync(factory, capability: true);

        using JsonDocument report = await RunScenarioAsync(client, FailureScenarioCatalog.MissingCapabilityGrant, HttpStatusCode.Forbidden);

        JsonElement decision = Assert.Single(report.RootElement.GetProperty("decisions").EnumerateArray());
        Assert.Equal("Denied", decision.GetProperty("outcome").GetString());
        Assert.Contains(ClaimEndpointCapabilityGrantValidator.DeniedReasonCode, ReadStrings(decision, "reasonCodes"));
        Assert.Equal("not-run", report.RootElement.GetProperty("actionBody").GetString());
        Assert.Contains(FailureScenarioFaultPoints.CapabilityClaimWithheld, ReadFaults(report));
        Assert.Equal(0, report.RootElement.GetProperty("mutationAuditRecordCount").GetInt32());
    }

    /// <summary>
    /// Verifies that, with signed capability tokens enabled, the capability endpoint called without a token is denied
    /// with AsiBackbone's missing-grant reason code.
    /// </summary>
    [Fact]
    public async Task MissingCapabilityGrant_WithSignedTokens_IsDeniedForTheMissingToken()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: true);
        using HttpClient client = await SignInAsync(factory, capability: true);

        using JsonDocument report = await RunScenarioAsync(client, FailureScenarioCatalog.MissingCapabilityGrant, HttpStatusCode.Forbidden);

        JsonElement decision = Assert.Single(report.RootElement.GetProperty("decisions").EnumerateArray());
        Assert.Equal("Denied", decision.GetProperty("outcome").GetString());
        Assert.Contains(SignedCapabilityGrantReasonCodes.Missing, ReadStrings(decision, "reasonCodes"));
        Assert.Equal("not-run", report.RootElement.GetProperty("actionBody").GetString());
    }

    /// <summary>
    /// Verifies that a constraint that throws fails closed: the request is denied with AsiBackbone's constraint-exception
    /// reason code, the denial is recorded, and no record is created.
    /// </summary>
    [Fact]
    public async Task ConstraintThrows_FailsClosedWithTheConstraintExceptionReasonCode()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = await SignInAsync(factory);

        using JsonDocument report = await RunScenarioAsync(client, FailureScenarioCatalog.ConstraintThrows, HttpStatusCode.Forbidden);

        JsonElement decision = Assert.Single(report.RootElement.GetProperty("decisions").EnumerateArray());
        Assert.Equal("Denied", decision.GetProperty("outcome").GetString());
        // Verified against the package: AsiBackbone's default, not the "policy.constraint_exception_as_denial" the issue
        // proposed.
        Assert.Equal("asibackbone.policy.constraint_exception", GovernancePolicyOptions.DefaultConstraintExceptionReasonCode);
        Assert.Contains(GovernancePolicyOptions.DefaultConstraintExceptionReasonCode, ReadStrings(decision, "reasonCodes"));
        Assert.Contains(FailureScenarioFaultPoints.ConstraintThrew, ReadFaults(report));
        Assert.Equal("not-run", report.RootElement.GetProperty("actionBody").GetString());
        Assert.Equal(0, await CountAsync(factory, static db => db.GovernedOperationRecords));
    }

    /// <summary>
    /// Verifies that a failed ledger append fails the request closed with
    /// <see cref="GovernanceEvidencePersistenceException" /> and the ledger's reason code, before the action body runs,
    /// leaving no ledger row and no record.
    /// </summary>
    [Fact]
    public async Task LedgerAppendFails_FailsClosedBeforeTheActionBody()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = await SignInAsync(factory);

        using JsonDocument report = await RunScenarioAsync(client, FailureScenarioCatalog.LedgerAppendFails, HttpStatusCode.InternalServerError);

        JsonElement run = report.RootElement.GetProperty("run");
        Assert.Equal(nameof(GovernanceEvidencePersistenceException), run.GetProperty("exceptionType").GetString());
        Assert.Equal("asi_backbone.audit_ledger.append_failed", run.GetProperty("exceptionReasonCode").GetString());
        Assert.Contains(FailureScenarioFaultPoints.LedgerAppend, ReadFaults(report));
        Assert.Empty(report.RootElement.GetProperty("decisions").EnumerateArray());
        Assert.Equal("not-run", report.RootElement.GetProperty("actionBody").GetString());
        Assert.Equal(0, await CountAsync(factory, static db => db.AsiBackboneAuditLedgerRecords));
        Assert.Equal(0, await CountAsync(factory, static db => db.GovernedOperationRecords));
    }

    /// <summary>
    /// Verifies that a failing lifecycle append leaves the request path unaffected, that the outbox shows the retryable
    /// failure with its attempt count and retry schedule, and that the entry is delivered, with its lifecycle event, once
    /// the fault is cleared.
    /// </summary>
    [Fact]
    public async Task OutboxDeliveryFails_RetriesThenRecoversAfterTheFaultIsCleared()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = await SignInAsync(factory);

        (string runId, JsonDocument created) = await ArmAndSendAsync(client, FailureScenarioCatalog.OutboxDeliveryFails, HttpStatusCode.Created);
        created.Dispose();
        Assert.Equal(1, await CountAsync(factory, static db => db.GovernedOperationRecords));

        ApplicationAuditCompletionDispatchSummary failed = await DispatchAsync(factory);
        Assert.Equal(1, failed.RetryScheduledCount);

        using (JsonDocument retrying = await GetRunAsync(client, runId))
        {
            JsonElement outbox = retrying.RootElement.GetProperty("outbox");
            Assert.Equal(ApplicationAuditCompletionOutboxStatuses.RetryableFailure, outbox.GetProperty("status").GetString());
            Assert.Equal(1, outbox.GetProperty("retryCount").GetInt32());
            Assert.True(outbox.GetProperty("maxRetryAttempts").GetInt32() > 1);
            Assert.Equal("lifecycle-persistence-failed", outbox.GetProperty("lastErrorCode").GetString());
            Assert.Equal(JsonValueKind.String, outbox.GetProperty("nextAttemptUtc").ValueKind);
            Assert.False(outbox.GetProperty("lifecycleEventAppended").GetBoolean());
            Assert.True(retrying.RootElement.GetProperty("run").GetProperty("deliveryFaultActive").GetBoolean());
            Assert.Contains(FailureScenarioFaultPoints.LifecycleAppend, ReadFaults(retrying));
            Assert.Equal("ran", retrying.RootElement.GetProperty("actionBody").GetString());
        }

        using HttpResponseMessage cleared = await PostAsync(client, $"{FailureScenarioServiceExtensions.RoutePrefix}/runs/{runId}/clear-delivery-fault");
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);

        // The test configures a one-millisecond base retry delay, so the entry is ready again almost at once.
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        ApplicationAuditCompletionDispatchSummary recovered = await DispatchAsync(factory);
        Assert.Equal(1, recovered.DeliveredCount);

        using JsonDocument delivered = await GetRunAsync(client, runId);
        JsonElement deliveredOutbox = delivered.RootElement.GetProperty("outbox");
        Assert.Equal(ApplicationAuditCompletionOutboxStatuses.Delivered, deliveredOutbox.GetProperty("status").GetString());
        Assert.True(deliveredOutbox.GetProperty("lifecycleEventAppended").GetBoolean());
        Assert.False(delivered.RootElement.GetProperty("run").GetProperty("deliveryFaultActive").GetBoolean());
        Assert.Contains(
            delivered.RootElement.GetProperty("timeline").GetProperty("entries").EnumerateArray(),
            static entry => entry.GetProperty("kind").GetString() == "lifecycle-event");

        using HttpResponseMessage clearedAgain = await PostAsync(client, $"{FailureScenarioServiceExtensions.RoutePrefix}/runs/{runId}/clear-delivery-fault");
        Assert.Equal(HttpStatusCode.Conflict, clearedAgain.StatusCode);
    }

    /// <summary>
    /// Verifies that when the mutation fails after an allowed, persisted decision, the ledger keeps the allowed
    /// decision, the mutation and its audit roll back, no outbox entry is staged, and reconciliation has no batch to
    /// report on.
    /// </summary>
    [Fact]
    public async Task ExecutionFailsAfterApproval_LeavesAnAllowedDecisionWithNoExecutionEvidence()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = await SignInAsync(factory);

        using JsonDocument report = await RunScenarioAsync(
            client,
            FailureScenarioCatalog.ExecutionFailsAfterApproval,
            HttpStatusCode.InternalServerError);

        JsonElement decision = Assert.Single(report.RootElement.GetProperty("decisions").EnumerateArray());
        Assert.Equal("Allowed", decision.GetProperty("outcome").GetString());
        Assert.Equal("ran-rolled-back", report.RootElement.GetProperty("actionBody").GetString());
        Assert.Contains(FailureScenarioFaultPoints.MutationSave, ReadFaults(report));
        Assert.Equal(0, report.RootElement.GetProperty("mutationAuditRecordCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("outbox").ValueKind);
        Assert.DoesNotContain(
            report.RootElement.GetProperty("timeline").GetProperty("entries").EnumerateArray(),
            static entry => entry.GetProperty("kind").GetString() is "lifecycle-event" or "mutation-audit");

        JsonElement reconciliation = report.RootElement.GetProperty("reconciliation");
        Assert.Empty(reconciliation.GetProperty("mutationBatchIds").EnumerateArray());
        Assert.Empty(reconciliation.GetProperty("findingReasonCodes").EnumerateArray());
        Assert.Contains("allowed decision with no lifecycle event", reconciliation.GetProperty("explanation").GetString(), StringComparison.Ordinal);

        Assert.Equal(1, await CountAsync(factory, static db => db.AsiBackboneAuditLedgerRecords));
        Assert.Equal(0, await CountAsync(factory, static db => db.GovernedOperationRecords));
        Assert.Equal(0, await CountAsync(factory, static db => db.AuditRecords));
        Assert.Equal(0, await CountAsync(factory, static db => db.ApplicationAuditCompletionOutboxEntries));
    }

    /// <summary>
    /// Verifies that changing a field of an in-memory copy of a persisted decision record is detected as a hash
    /// mismatch, that an unchanged copy made the same way verifies, and that the stored row is never modified.
    /// </summary>
    [Fact]
    public async Task EvidenceTampering_DetectsTheChangedCopy_AndNeverModifiesTheStoredRow()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = await SignInAsync(factory);

        using (HttpResponseMessage refused = await PostAsync(client, $"{FailureScenarioServiceExtensions.RoutePrefix}/{FailureScenarioCatalog.EvidenceTampering}"))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        }

        using (HttpResponseMessage created = await PostAsync(client, FailureScenarioCatalog.RecordsPath, new() { ["Title"] = "Tampering source" }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        (string recordId, string storedOutcome, string? storedHash) = await ReadSingleLedgerRecordAsync(factory);

        using HttpResponseMessage response = await PostAsync(client, $"{FailureScenarioServiceExtensions.RoutePrefix}/{FailureScenarioCatalog.EvidenceTampering}");
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using var result = JsonDocument.Parse(body);
        JsonElement tampering = result.RootElement.GetProperty("tampering");

        Assert.Equal(recordId, tampering.GetProperty("recordId").GetString());
        Assert.Equal("outcome", tampering.GetProperty("field").GetString());
        Assert.Equal(storedOutcome, tampering.GetProperty("storedValue").GetString());
        Assert.NotEqual(storedOutcome, tampering.GetProperty("tamperedValue").GetString());
        Assert.Equal(GovernanceEvidenceVerificationStatuses.Verified, tampering.GetProperty("unchangedCopy").GetProperty("status").GetString());
        Assert.Equal(GovernanceEvidenceVerificationStatuses.Mismatch, tampering.GetProperty("tamperedCopy").GetProperty("status").GetString());
        Assert.Equal(GovernanceEvidenceVerificationStatuses.Verified, tampering.GetProperty("storedRecord").GetProperty("status").GetString());

        (string afterId, string afterOutcome, string? afterHash) = await ReadSingleLedgerRecordAsync(factory);
        Assert.Equal((recordId, storedOutcome, storedHash), (afterId, afterOutcome, afterHash));
    }

    /// <summary>
    /// Verifies that a valid token presented with a payload other than the one it was issued for is denied with
    /// AsiBackbone's resource-mismatch reason code, and that the action does not run.
    /// </summary>
    [Fact]
    public async Task PayloadTampering_UnderAValidToken_IsDeniedForTheBindingMismatch()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: true);
        using HttpClient client = await SignInAsync(factory, capability: true);

        using JsonDocument report = await RunScenarioAsync(client, FailureScenarioCatalog.PayloadTampering, HttpStatusCode.Forbidden);

        JsonElement decision = Assert.Single(report.RootElement.GetProperty("decisions").EnumerateArray());
        Assert.Equal("Denied", decision.GetProperty("outcome").GetString());
        Assert.Contains(SignedCapabilityGrantReasonCodes.ResourceMismatch, ReadStrings(decision, "reasonCodes"));
        Assert.Equal("not-run", report.RootElement.GetProperty("actionBody").GetString());
    }

    /// <summary>
    /// Verifies that a token issued from a clock set back past its lifetime is denied as expired, and that the action
    /// does not run.
    /// </summary>
    [Fact]
    public async Task ExpiredToken_IsDeniedAsExpired()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: true);
        using HttpClient client = await SignInAsync(factory, capability: true);

        using JsonDocument report = await RunScenarioAsync(client, FailureScenarioCatalog.ExpiredToken, HttpStatusCode.Forbidden);

        JsonElement decision = Assert.Single(report.RootElement.GetProperty("decisions").EnumerateArray());
        Assert.Equal("Denied", decision.GetProperty("outcome").GetString());
        Assert.Contains(SignedCapabilityGrantReasonCodes.Expired, ReadStrings(decision, "reasonCodes"));
        Assert.Equal("not-run", report.RootElement.GetProperty("actionBody").GetString());
    }

    /// <summary>
    /// Verifies that the token scenarios are listed as unavailable, with an explanation, while signed capability tokens
    /// are disabled, and that asking to run one is refused.
    /// </summary>
    [Fact]
    public async Task TokenScenarios_AreUnavailableWithAnExplanation_WhileTokensAreDisabled()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: false);
        using HttpClient client = await SignInAsync(factory, capability: true);

        using HttpResponseMessage response = await client.GetAsync(FailureScenarioServiceExtensions.RoutePrefix, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scenarios = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        JsonElement[] listed = [.. scenarios.RootElement.EnumerateArray()];

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], listed.Select(static scenario => scenario.GetProperty("number").GetInt32()));
        foreach (JsonElement scenario in listed)
        {
            bool needsTokens = scenario.GetProperty("number").GetInt32() >= 7;
            Assert.Equal(!needsTokens, scenario.GetProperty("available").GetBoolean());
            if (needsTokens)
            {
                Assert.Contains("Signed capability tokens are disabled", scenario.GetProperty("unavailableReason").GetString(), StringComparison.Ordinal);
            }
        }

        using HttpResponseMessage refused = await PostAsync(client, $"{FailureScenarioServiceExtensions.RoutePrefix}/{FailureScenarioCatalog.PayloadTampering}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    /// <summary>
    /// Verifies that a run disarms after one request: the same run identifier is refused before governance runs, and
    /// a request without the run header is unaffected while a run is armed.
    /// </summary>
    [Fact]
    public async Task Run_DisarmsAfterOneRequest_AndLeavesOtherRequestsUnaffected()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = await SignInAsync(factory);

        using JsonDocument armed = await ArmAsync(client, FailureScenarioCatalog.LedgerAppendFails);
        JsonElement request = armed.RootElement.GetProperty("run").GetProperty("request");

        // An ordinary request, without the run header, is not touched by the armed seam.
        using (HttpResponseMessage ordinary = await PostAsync(client, FailureScenarioCatalog.RecordsPath, new() { ["Title"] = "Ordinary" }))
        {
            Assert.Equal(HttpStatusCode.Created, ordinary.StatusCode);
        }

        using (HttpResponseMessage first = await SendArmedRequestAsync(client, request))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, first.StatusCode);
        }

        using (HttpResponseMessage second = await SendArmedRequestAsync(client, request))
        {
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        }

        // Only the ordinary request left evidence: the armed request's append failed, and the replay never reached governance.
        Assert.Equal(1, await CountAsync(factory, static db => db.AsiBackboneAuditLedgerRecords));
        Assert.Equal(1, await CountAsync(factory, static db => db.GovernedOperationRecords));

        using HttpResponseMessage unknown = await SendArmedRequestAsync(client, request, runId: Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);
    }

    /// <summary>
    /// Verifies that the failure-scenario routes are administrator-only: a non-administrator is sent to the access-denied
    /// page and arms nothing.
    /// </summary>
    [Fact]
    public async Task Scenarios_NonAdministrator_IsForbidden()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();
        using HttpClient client = await SignInAsync(factory, administrator: false);

        // The demo identity signs in with the cookie scheme, which answers a forbidden request with its access-denied page.
        using HttpResponseMessage list = await client.GetAsync(FailureScenarioServiceExtensions.RoutePrefix, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, list.StatusCode);
        Assert.Equal("/Account/AccessDenied", list.Headers.Location?.AbsolutePath);

        using HttpResponseMessage start = await PostAsync(client, $"{FailureScenarioServiceExtensions.RoutePrefix}/{FailureScenarioCatalog.ConstraintThrows}");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        Assert.Equal("/Account/AccessDenied", start.Headers.Location?.AbsolutePath);
        Assert.Equal(0, await CountAsync(factory, static db => db.AsiBackboneAuditLedgerRecords));
    }

    /// <summary>
    /// Verifies that the scenarios, the throwing constraint, the save interceptor, and the routes are absent outside
    /// Development, and in Development while the switch is off or the demonstration database is not in use.
    /// </summary>
    /// <param name="environmentName">The host environment.</param>
    /// <param name="scenariosEnabled">The failure-scenario switch.</param>
    /// <param name="demoIdentityEnabled">The demo identity switch, which selects the demonstration database.</param>
    [Theory]
    [InlineData("Staging", true, true)]
    [InlineData("Production", true, true)]
    [InlineData("Development", false, true)]
    [InlineData("Development", true, false)]
    public async Task Scenarios_OutsideAnActiveDevelopmentHost_AreNotRegisteredOrMapped(
        string environmentName,
        bool scenariosEnabled,
        bool demoIdentityEnabled)
    {
        using WebApplicationFactory<Program> factory = await _database.ApplyAsync(
            ApplicationWebApplicationFactory
                .CreateAllowingAnonymousAccess(new Dictionary<string, string?>(), environmentName)
                .WithWebHostBuilder(builder => PinFeatures(builder, scenariosEnabled, demoIdentityEnabled, tokensEnabled: false)),
            migrate: false);

        Assert.Null(factory.Services.GetService<FailureScenarioRegistry>());
        Assert.Null(factory.Services.GetService<FailureScenarioSaveChangesInterceptor>());
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            Assert.Null(scope.ServiceProvider.GetService<IFailureScenarioService>());
            Assert.DoesNotContain(
                scope.ServiceProvider.GetServices<IGovernanceConstraint<GovernanceEvaluationContext>>(),
                static constraint => constraint is FailureScenarioThrowingConstraint);
        }

        using HttpClient client = factory.CreateHttpsClient();
        using HttpResponseMessage response = await client.GetAsync(FailureScenarioServiceExtensions.RoutePrefix, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Verifies that the explorer shows the Failure scenarios tab to administrators only.
    /// </summary>
    [Fact]
    public async Task Explorer_ShowsTheScenariosOnlyToAdministrators()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync();

        using HttpClient administrator = await SignInAsync(factory);
        string administratorPage = await administrator.GetStringAsync("/governance-explorer", TestContext.Current.CancellationToken);
        Assert.Contains("id=\"explorer-tab-scenarios\"", administratorPage, StringComparison.Ordinal);
        Assert.Contains("data-failure-scenarios=\"true\"", administratorPage, StringComparison.Ordinal);

        using HttpClient reader = await SignInAsync(factory, administrator: false);
        string readerPage = await reader.GetStringAsync("/governance-explorer", TestContext.Current.CancellationToken);
        Assert.DoesNotContain("id=\"explorer-tab-scenarios\"", readerPage, StringComparison.Ordinal);
        Assert.Contains("data-failure-scenarios=\"false\"", readerPage, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _database.Dispose();
    }

    private Task<WebApplicationFactory<Program>> CreateFactoryAsync(bool tokensEnabled = false)
    {
        Dictionary<string, string?> configuration = new()
        {
            // The tests dispatch the outbox themselves; the worker polls once at startup and then waits an hour.
            [$"{_outboxSection}:Enabled"] = "true",
            [$"{_outboxSection}:PollInterval"] = "01:00:00",
            [$"{_outboxSection}:MaximumCycleRetryDelay"] = "01:00:00",
            [$"{_outboxSection}:BaseRetryDelay"] = "00:00:00.001"
        };

        return _database.ApplyAsync(ApplicationWebApplicationFactory
            .CreateAllowingAnonymousAccess(configuration, Environments.Development)
            .WithWebHostBuilder(builder => PinFeatures(builder, scenariosEnabled: true, demoIdentityEnabled: true, tokensEnabled)));
    }

    private static void PinFeatures(IWebHostBuilder builder, bool scenariosEnabled, bool demoIdentityEnabled, bool tokensEnabled)
    {
        // Feature switches are read during service registration, before test configuration sources are applied, so
        // they are pinned as host settings. That also keeps local appsettings.Development.json values from changing what
        // these tests exercise.
        builder
            .UseSetting(_scenariosEnabledKey, scenariosEnabled ? "true" : "false")
            .UseSetting($"{DemoIdentityOptions.SectionName}:Enabled", demoIdentityEnabled ? "true" : "false")
            .UseSetting("ReferenceApplication:PipelineInspector:Enabled", "false")
            .UseSetting("ReferenceApplication:DecisionReplay:Enabled", "false")
            .UseSetting("ReferenceApplication:PolicySimulator:Enabled", "false")
            .UseSetting("ReferenceApplication:GovernanceLoadDemonstration:Enabled", "false")
            .UseSetting($"{_tokensSection}:Enabled", tokensEnabled ? "true" : "false");

        if (tokensEnabled)
        {
            builder
                .UseSetting($"{_tokensSection}:SigningKeyPem", _signingKey.Value.PrivateKeyPem)
                .UseSetting($"{_tokensSection}:VerificationKeys:0:KeyVersion", "1")
                .UseSetting($"{_tokensSection}:VerificationKeys:0:PublicKeyPem", _signingKey.Value.PublicKeyPem);
        }
    }

    private static async Task<HttpClient> SignInAsync(
        WebApplicationFactory<Program> factory,
        bool administrator = true,
        bool capability = false)
    {
        HttpClient client = factory.CreateHttpsClient();
        Dictionary<string, string> form = new()
        {
            [_antiforgeryField] = await GetAntiforgeryTokenAsync(client),
            ["returnUrl"] = "/"
        };

        if (administrator)
        {
            form["administrator"] = bool.TrueString;
        }

        if (capability)
        {
            form["consequentialCapability"] = bool.TrueString;
        }

        using HttpResponseMessage signIn = await client.PostAsync(
            DemoIdentityOptions.SignInPath,
            new FormUrlEncodedContent(form),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        return client;
    }

    private static async Task<JsonDocument> RunScenarioAsync(HttpClient client, string scenarioId, HttpStatusCode expectedStatus)
    {
        (string runId, JsonDocument report) = await ArmAndSendAsync(client, scenarioId, expectedStatus);
        report.Dispose();
        return await GetRunAsync(client, runId);
    }

    private static async Task<(string RunId, JsonDocument Report)> ArmAndSendAsync(
        HttpClient client,
        string scenarioId,
        HttpStatusCode expectedStatus)
    {
        using JsonDocument armed = await ArmAsync(client, scenarioId);
        JsonElement run = armed.RootElement.GetProperty("run");
        string runId = run.GetProperty("runId").GetString()!;
        Assert.Equal(FailureScenarioRunStatuses.Armed, run.GetProperty("status").GetString());

        using (HttpResponseMessage response = await SendArmedRequestAsync(client, run.GetProperty("request")))
        {
            string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(response.StatusCode == expectedStatus, $"{scenarioId} returned {(int)response.StatusCode}: {body}");
        }

        JsonDocument report = await GetRunAsync(client, runId);
        JsonElement state = report.RootElement.GetProperty("run");
        Assert.Equal(FailureScenarioRunStatuses.Completed, state.GetProperty("status").GetString());
        Assert.Equal((int)expectedStatus, state.GetProperty("statusCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("request").ValueKind);
        return (runId, report);
    }

    private static async Task<JsonDocument> ArmAsync(HttpClient client, string scenarioId)
    {
        using HttpResponseMessage response = await PostAsync(client, $"{FailureScenarioServiceExtensions.RoutePrefix}/{scenarioId}");
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Arming {scenarioId} returned {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    private static async Task<HttpResponseMessage> SendArmedRequestAsync(HttpClient client, JsonElement request, string? runId = null)
    {
        Dictionary<string, string> fields = new()
        {
            [_antiforgeryField] = await GetAntiforgeryTokenAsync(client)
        };
        foreach (JsonProperty field in request.GetProperty("fields").EnumerateObject())
        {
            fields[field.Name] = field.Value.GetString()!;
        }

        using var message = new HttpRequestMessage(new HttpMethod(request.GetProperty("method").GetString()!), request.GetProperty("path").GetString())
        {
            Content = new FormUrlEncodedContent(fields)
        };
        message.Headers.Accept.ParseAdd("application/json");
        foreach (JsonProperty header in request.GetProperty("headers").EnumerateObject())
        {
            string value = runId is not null && string.Equals(header.Name, FailureScenarioOptions.RunHeaderName, StringComparison.OrdinalIgnoreCase)
                ? runId
                : header.Value.GetString()!;
            message.Headers.Add(header.Name, value);
        }

        return await client.SendAsync(message, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonDocument> GetRunAsync(HttpClient client, string runId)
    {
        using HttpResponseMessage response = await client.GetAsync(
            $"{FailureScenarioServiceExtensions.RoutePrefix}/runs/{runId}",
            TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"The run report returned {(int)response.StatusCode}: {body}");
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        return JsonDocument.Parse(body);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, Dictionary<string, string>? fields = null)
    {
        Dictionary<string, string> form = new(fields ?? [])
        {
            [_antiforgeryField] = await GetAntiforgeryTokenAsync(client)
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
        message.Headers.Accept.ParseAdd("application/json");
        return await client.SendAsync(message, TestContext.Current.CancellationToken);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync(
            "/test/authentication/antiforgery-token",
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<ApplicationAuditCompletionDispatchSummary> DispatchAsync(WebApplicationFactory<Program> factory)
    {
        // The test stands in for the background worker: this is the outbox's own delivery path.
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IApplicationAuditCompletionOutboxDispatcher>()
            .DispatchReadyAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<int> CountAsync<TEntity>(
        WebApplicationFactory<Program> factory,
        Func<ApplicationDbContext, IQueryable<TEntity>> select)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        return await select(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>())
            .CountAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<(string RecordId, string Outcome, string? RecordHash)> ReadSingleLedgerRecordAsync(
        WebApplicationFactory<Program> factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuditLedgerRecordEntity record = await scope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>()
            .AsiBackboneAuditLedgerRecords
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        return (record.RecordId, record.Outcome, record.RecordHash);
    }

    private static string[] ReadStrings(JsonElement element, string propertyName)
    {
        return [.. element.GetProperty(propertyName).EnumerateArray().Select(static value => value.GetString()!)];
    }

    private static string[] ReadFaults(JsonDocument report)
    {
        return [.. report.RootElement.GetProperty("run").GetProperty("faults").EnumerateArray()
            .Select(static fault => fault.GetProperty("point").GetString()!)];
    }
}
