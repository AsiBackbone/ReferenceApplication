using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AsiBackbone.Core.Audit;
using AsiBackbone.Core.Decisions;
using AsiBackbone.Core.HostIntegration;
using AsiBackbone.EntityFrameworkCore.Audit;
using AsiBackbone.EntityFrameworkCore.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    private Task<WebApplicationFactory<Program>> CreateFactoryAsync(bool migrate = true)
    {
        return _database.ApplyAsync(
            ApplicationWebApplicationFactory.CreateAllowingAnonymousAccess(new Dictionary<string, string?>
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
            })
            .WithWebHostBuilder(builder => builder.ConfigureServices(services => services
                .AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(_testAuthenticationScheme, _ => { }))),
            migrate);
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
