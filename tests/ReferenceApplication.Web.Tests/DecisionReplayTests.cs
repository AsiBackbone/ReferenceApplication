using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using AsiBackbone.AspNetCore.CapabilityGrants;
using AsiBackbone.Core.Actors;
using AsiBackbone.Core.Audit;
using AsiBackbone.Core.Decisions;
using AsiBackbone.Core.Results;
using AsiBackbone.EntityFrameworkCore.Audit;
using AsiBackbone.EntityFrameworkCore.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ReferenceApplication.Infrastructure.Data;
using ReferenceApplication.Web.Authentication.Claims;
using ReferenceApplication.Web.Extensions;
using ReferenceApplication.Web.Governance;
using ReferenceApplication.Web.Governance.Replay;
using ReferenceApplication.Web.Tests.Extensions;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies the Development-only decision replay: lookup by receipt identifier, reproduction of each sample decision
/// under the current policy, labelled what-if replays, policy-hash mismatch notices, that replay persists nothing, and
/// that it is reachable only by administrators in Development.
/// </summary>
public sealed class DecisionReplayTests : IDisposable
{
    private const string _authenticationScheme = "DecisionReplayTestAuthentication";
    private const string _userHeader = "X-Test-User";
    private const string _roleHeader = "X-Test-Role";
    private const string _permissionHeader = "X-Test-Permission";
    private const string _user = "replay-user";
    private const string _administratorRole = "administrator";
    private const string _antiforgeryField = "__RequestVerificationToken";
    private const string _scope = ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope;
    private const string _payload = "Decision replay record";

    // Generated for the test run, so no signing key is checked in.
    private static readonly Lazy<(string PrivateKeyPem, string PublicKeyPem)> _signingKey = new(static () =>
    {
        using var rsa = RSA.Create(2048);
        return (rsa.ExportPkcs8PrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    });

    private readonly TestSqliteDatabase _database = TestSqliteDatabase.Create();

    /// <summary>
    /// Verifies that, for each sample operation and actor, replaying the persisted decision under the current policy
    /// reproduces its outcome and reason codes exactly, as a determined re-verification.
    /// </summary>
    /// <param name="operation">The governed operation.</param>
    /// <param name="administrator">Whether the actor holds the administrator role.</param>
    /// <param name="capability">Whether the actor holds the consequential-execution capability.</param>
    [Theory]
    [InlineData("administrator", true, false)]
    [InlineData("administrator", false, false)]
    [InlineData("capability", false, true)]
    [InlineData("capability", false, false)]
    [InlineData("consequential", true, true)]
    [InlineData("consequential", true, false)]
    [InlineData("consequential", false, true)]
    [InlineData("records", true, false)]
    [InlineData("records", false, false)]
    public async Task Replay_UnderCurrentPolicy_ReproducesTheRecordedDecision(
        string operation,
        bool administrator,
        bool capability)
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: false);
        using HttpClient client = factory.CreateHttpsClient();

        _ = await RunOperationAsync(
            client,
            operation,
            role: administrator ? _administratorRole : null,
            permission: capability ? _scope : null);
        AuditLedgerRecord record = await ReadSingleRecordAsync(factory);

        using JsonDocument replay = await ReplayAsync(client, record.DecisionReceiptId);
        JsonElement current = replay.RootElement.GetProperty("current");
        JsonElement branch = Assert.Single(current.GetProperty("branches").EnumerateArray());

        Assert.False(current.GetProperty("hypothetical").GetBoolean());
        Assert.False(current.GetProperty("partial").GetBoolean());
        Assert.Equal(record.Outcome, branch.GetProperty("outcome").GetString());
        Assert.Equal(record.ReasonCodes, ReadStrings(branch.GetProperty("reasonCodes")));
        Assert.True(branch.GetProperty("difference").GetProperty("matches").GetBoolean());
        Assert.Equal(record.ActorId, branch.GetProperty("receipt").GetProperty("actorId").GetString());
        Assert.Equal(JsonValueKind.Null, replay.RootElement.GetProperty("candidate").ValueKind);
    }

    /// <summary>
    /// Verifies that with signed capability tokens enabled, a decision denied for a missing token replays to the same
    /// denial from the recorded grant outcome, without verifying a token again.
    /// </summary>
    [Fact]
    public async Task Replay_WithTokensEnabled_ReproducesARecordedGrantDenial()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: true);
        using HttpClient client = factory.CreateHttpsClient();

        _ = await RunOperationAsync(client, "capability", role: null, permission: _scope);
        AuditLedgerRecord record = await ReadSingleRecordAsync(factory);
        Assert.Contains(SignedCapabilityGrantReasonCodes.Missing, record.ReasonCodes);

        using JsonDocument replay = await ReplayAsync(client, record.DecisionReceiptId);
        JsonElement branch = Assert.Single(replay.RootElement.GetProperty("current").GetProperty("branches").EnumerateArray());

        Assert.True(branch.GetProperty("difference").GetProperty("matches").GetBoolean());
        Assert.Equal(DecisionReplayService.RecordedGrantModel, branch.GetProperty("capability").GetProperty("model").GetString());
    }

    /// <summary>
    /// Verifies that a decision is found by its receipt identifier with its reconstruction, and that an unknown
    /// identifier returns not found for both lookup and replay.
    /// </summary>
    [Fact]
    public async Task Find_ByReceiptId_ReturnsDecision_AndUnknownIdReturnsNotFound()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: false);
        using HttpClient client = factory.CreateHttpsClient();
        _ = await RunOperationAsync(client, "administrator", _administratorRole, permission: null);
        AuditLedgerRecord record = await ReadSingleRecordAsync(factory);

        using HttpResponseMessage found = await GetAsync(
            client,
            $"{DecisionReplayServiceExtensions.DecisionsPath}/{record.DecisionReceiptId}",
            _administratorRole);
        using var details = JsonDocument.Parse(await found.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        JsonElement root = details.RootElement;
        Assert.Equal(record.DecisionReceiptId, root.GetProperty("decisionReceiptId").GetString());
        Assert.Equal("administrator", root.GetProperty("operationKey").GetString());
        Assert.True(root.GetProperty("policy").GetProperty("matches").GetBoolean());
        Assert.Equal(
            GovernanceEvidenceVerificationStatuses.Verified,
            root.GetProperty("evidence").GetProperty("status").GetString());
        JsonElement administratorInput = root.GetProperty("inputs").EnumerateArray()
            .Single(static input => input.GetProperty("key").GetString() == DecisionReplayFactKeys.Administrator);
        Assert.Equal(DecisionReplayInputStatuses.Inferred, administratorInput.GetProperty("status").GetString());
        Assert.Equal("yes", administratorInput.GetProperty("value").GetString());
        JsonElement payloadInput = root.GetProperty("inputs").EnumerateArray()
            .Single(static input => input.GetProperty("key").GetString() == "payload");
        Assert.Equal(DecisionReplayInputStatuses.Unrecoverable, payloadInput.GetProperty("status").GetString());

        using HttpResponseMessage unknown = await GetAsync(
            client,
            $"{DecisionReplayServiceExtensions.DecisionsPath}/not-a-receipt",
            _administratorRole);
        using HttpResponseMessage unknownReplay = await PostReplayAsync(
            client,
            "not-a-receipt",
            await GetAntiforgeryTokenAsync(client, _administratorRole),
            disabledConstraints: [],
            candidatePolicy: false,
            role: _administratorRole);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownReplay.StatusCode);
    }

    /// <summary>
    /// Verifies that leaving a constraint out of the candidate changes the replayed outcome and labels the candidate as
    /// hypothetical, while the current-policy replay still reproduces the recorded denial.
    /// </summary>
    [Fact]
    public async Task Replay_WithConstraintLeftOut_ShowsChangedOutcomeAsHypothetical()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: false);
        using HttpClient client = factory.CreateHttpsClient();
        _ = await RunOperationAsync(client, "administrator", role: null, permission: null);
        AuditLedgerRecord record = await ReadSingleRecordAsync(factory);

        using JsonDocument replay = await ReplayAsync(
            client,
            record.DecisionReceiptId,
            disabledConstraints: [AdministratorRoleConstraint.ConstraintName]);
        JsonElement candidate = replay.RootElement.GetProperty("candidate");
        JsonElement branch = Assert.Single(candidate.GetProperty("branches").EnumerateArray());

        Assert.True(Assert.Single(replay.RootElement.GetProperty("current").GetProperty("branches").EnumerateArray())
            .GetProperty("difference").GetProperty("matches").GetBoolean());
        Assert.True(candidate.GetProperty("hypothetical").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(candidate.GetProperty("hypotheticalReason").GetString()));
        Assert.Equal(
            AdministratorRoleConstraint.ConstraintName,
            Assert.Single(ReadStrings(candidate.GetProperty("disabledConstraints"))));
        Assert.True(branch.GetProperty("difference").GetProperty("outcomeChanged").GetBoolean());
        Assert.Equal(nameof(GovernanceDecisionOutcome.Allowed), branch.GetProperty("outcome").GetString());
        Assert.Contains(
            AdministratorRoleConstraint.DeniedReasonCode,
            ReadStrings(branch.GetProperty("difference").GetProperty("removedReasonCodes")));
    }

    /// <summary>
    /// Verifies that the stand-in next policy version replays under its own version and hash and changes the outcome
    /// of an audited-record decision.
    /// </summary>
    [Fact]
    public async Task Replay_UnderCandidatePolicy_UsesItsVersionAndChangesTheOutcome()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: false);
        using HttpClient client = factory.CreateHttpsClient();
        _ = await RunOperationAsync(client, "records", _administratorRole, permission: null);
        AuditLedgerRecord record = await ReadSingleRecordAsync(factory);

        using JsonDocument replay = await ReplayAsync(client, record.DecisionReceiptId, candidatePolicy: true);
        JsonElement candidate = replay.RootElement.GetProperty("candidate");
        JsonElement branch = Assert.Single(candidate.GetProperty("branches").EnumerateArray());

        Assert.True(candidate.GetProperty("hypothetical").GetBoolean());
        Assert.Equal(CandidateGovernancePolicySet.Version, candidate.GetProperty("policyVersion").GetString());
        Assert.Equal(CandidateGovernancePolicySet.PolicyHash, candidate.GetProperty("policyHash").GetString());
        Assert.Equal(
            CandidateGovernancePolicySet.PolicyHash,
            branch.GetProperty("receipt").GetProperty("policyHash").GetString());
        Assert.NotEmpty(candidate.GetProperty("policyChanges").EnumerateArray());
        Assert.Equal(nameof(GovernanceDecisionOutcome.AcknowledgmentRequired), branch.GetProperty("outcome").GetString());
        Assert.Contains(
            CandidateDecisionPolicy.AuditedRecordAcknowledgmentReasonCode,
            ReadStrings(branch.GetProperty("difference").GetProperty("addedReasonCodes")));
    }

    /// <summary>
    /// Verifies that when the candidate needs a fact the ledger does not record, the replay is marked partial and shows
    /// the outcome under each value instead of inventing one.
    /// </summary>
    [Fact]
    public async Task Replay_NeedingAnUnrecordedFact_IsPartialWithABranchPerValue()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: false);
        using HttpClient client = factory.CreateHttpsClient();

        // The capability endpoint never checks the administrator role, so the ledger holds no evidence of it.
        _ = await RunOperationAsync(client, "capability", role: null, permission: _scope);
        AuditLedgerRecord record = await ReadSingleRecordAsync(factory);

        using JsonDocument replay = await ReplayAsync(client, record.DecisionReceiptId, candidatePolicy: true);
        JsonElement current = replay.RootElement.GetProperty("current");
        JsonElement candidate = replay.RootElement.GetProperty("candidate");

        Assert.False(current.GetProperty("partial").GetBoolean());
        Assert.Contains(DecisionReplayFactKeys.Administrator, ReadStrings(current.GetProperty("ignoredUnknownInputs")));
        Assert.True(candidate.GetProperty("partial").GetBoolean());
        Assert.Equal(
            DecisionReplayFactKeys.Administrator,
            Assert.Single(ReadStrings(candidate.GetProperty("decisiveUnknownInputs"))));

        JsonElement[] branches = [.. candidate.GetProperty("branches").EnumerateArray()];
        Assert.Equal(2, branches.Length);
        JsonElement withoutRole = branches.Single(static branch =>
            !branch.GetProperty("assumptions").GetProperty(DecisionReplayFactKeys.Administrator).GetBoolean());
        JsonElement withRole = branches.Single(static branch =>
            branch.GetProperty("assumptions").GetProperty(DecisionReplayFactKeys.Administrator).GetBoolean());
        Assert.Equal(nameof(GovernanceDecisionOutcome.Denied), withoutRole.GetProperty("outcome").GetString());
        Assert.Contains(
            CandidateCapabilityAdministratorConstraint.DeniedReasonCode,
            ReadStrings(withoutRole.GetProperty("reasonCodes")));
        Assert.Equal(record.Outcome, withRole.GetProperty("outcome").GetString());
    }

    /// <summary>
    /// Verifies that a decision recorded under a different policy hash is flagged in the list and the lookup before any
    /// replay, and that its current-policy replay is labelled as a what-if rather than a re-verification.
    /// </summary>
    [Fact]
    public async Task PolicyHashMismatch_IsSurfacedBeforeReplay()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: false);
        using HttpClient client = factory.CreateHttpsClient();
        _ = await RunOperationAsync(client, "administrator", _administratorRole, permission: null);
        AuditLedgerRecord current = await ReadSingleRecordAsync(factory);
        AuditLedgerRecord older = await AppendRecordUnderOlderPolicyAsync(factory, current);

        using HttpResponseMessage list = await GetAsync(client, DecisionReplayServiceExtensions.DecisionsPath, _administratorRole);
        using var page = JsonDocument.Parse(await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        JsonElement olderSummary = page.RootElement.GetProperty("decisions").EnumerateArray()
            .Single(summary => summary.GetProperty("decisionReceiptId").GetString() == older.DecisionReceiptId);
        Assert.False(olderSummary.GetProperty("policyMatchesCurrent").GetBoolean());

        using HttpResponseMessage found = await GetAsync(
            client,
            $"{DecisionReplayServiceExtensions.DecisionsPath}/{older.DecisionReceiptId}",
            _administratorRole);
        using var details = JsonDocument.Parse(await found.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        JsonElement policy = details.RootElement.GetProperty("policy");
        Assert.False(policy.GetProperty("matches").GetBoolean());
        Assert.Equal("0.9.0", policy.GetProperty("recordedVersion").GetString());
        Assert.Equal(ReferenceApplicationGovernancePolicySet.PolicyHash, policy.GetProperty("currentHash").GetString());
        Assert.Contains(
            policy.GetProperty("notices").EnumerateArray(),
            static notice => notice.GetString()!.Contains("not a re-verification", StringComparison.Ordinal));

        using JsonDocument replay = await ReplayAsync(client, older.DecisionReceiptId);
        Assert.True(replay.RootElement.GetProperty("current").GetProperty("hypothetical").GetBoolean());
    }

    /// <summary>
    /// Verifies that the list is newest first, pages with a cursor, and rejects an unknown cursor.
    /// </summary>
    [Fact]
    public async Task List_PagesNewestFirst()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: false);
        using HttpClient client = factory.CreateHttpsClient();
        int decisionCount = DecisionReplayOptions.PageSize + 2;
        for (int index = 0; index < decisionCount; index++)
        {
            _ = await RunOperationAsync(client, "administrator", _administratorRole, permission: null);
        }

        using HttpResponseMessage first = await GetAsync(client, DecisionReplayServiceExtensions.DecisionsPath, _administratorRole);
        using var firstPage = JsonDocument.Parse(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        DateTimeOffset[] firstTimes = [.. firstPage.RootElement.GetProperty("decisions").EnumerateArray()
            .Select(static summary => summary.GetProperty("occurredUtc").GetDateTimeOffset())];
        string cursor = firstPage.RootElement.GetProperty("nextCursor").GetString()!;

        using HttpResponseMessage second = await GetAsync(
            client,
            $"{DecisionReplayServiceExtensions.DecisionsPath}?before={Uri.EscapeDataString(cursor)}",
            _administratorRole);
        using var secondPage = JsonDocument.Parse(await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using HttpResponseMessage invalid = await GetAsync(
            client,
            $"{DecisionReplayServiceExtensions.DecisionsPath}?before=not-a-record",
            _administratorRole);

        Assert.Equal(DecisionReplayOptions.PageSize, firstTimes.Length);
        DateTimeOffset[] newestFirst = [.. firstTimes.OrderByDescending(static time => time)];
        Assert.Equal(newestFirst, firstTimes);
        Assert.Equal(2, secondPage.RootElement.GetProperty("decisions").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, secondPage.RootElement.GetProperty("nextCursor").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    /// <summary>
    /// Verifies that replays, including what-if and partial ones, leave the ledger, outbox, lifecycle, mutation-audit,
    /// application-record, and capability-use row counts unchanged.
    /// </summary>
    [Fact]
    public async Task Replay_LeavesEveryEvidenceRowCountUnchanged()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: false);
        using HttpClient client = factory.CreateHttpsClient();
        foreach (string operation in new[] { "administrator", "capability", "consequential", "records" })
        {
            _ = await RunOperationAsync(client, operation, _administratorRole, _scope);
        }

        EvidenceCounts before = await CountEvidenceAsync(factory);
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            string[] receiptIds = [.. await dbContext.AsiBackboneAuditLedgerRecords
                .AsNoTracking()
                .Select(static record => record.DecisionReceiptId ?? record.EventId)
                .ToListAsync(TestContext.Current.CancellationToken)];

            foreach (string receiptId in receiptIds)
            {
                using JsonDocument currentOnly = await ReplayAsync(client, receiptId);
                using JsonDocument whatIf = await ReplayAsync(
                    client,
                    receiptId,
                    disabledConstraints: [AdministratorRoleConstraint.ConstraintName],
                    candidatePolicy: true);
                Assert.Equal(JsonValueKind.Object, currentOnly.RootElement.GetProperty("current").ValueKind);
                Assert.Equal(JsonValueKind.Object, whatIf.RootElement.GetProperty("candidate").ValueKind);
            }
        }

        Assert.Equal(before, await CountEvidenceAsync(factory));
    }

    /// <summary>
    /// Verifies that a non-administrator cannot list, find, or replay decisions, and that a replay without an
    /// antiforgery token is rejected.
    /// </summary>
    [Fact]
    public async Task Routes_AreAdministratorOnly_AndReplayRequiresAntiforgery()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: false);
        using HttpClient client = factory.CreateHttpsClient();
        _ = await RunOperationAsync(client, "administrator", _administratorRole, permission: null);
        AuditLedgerRecord record = await ReadSingleRecordAsync(factory);

        using HttpResponseMessage list = await GetAsync(client, DecisionReplayServiceExtensions.DecisionsPath, "reader");
        using HttpResponseMessage find = await GetAsync(
            client,
            $"{DecisionReplayServiceExtensions.DecisionsPath}/{record.DecisionReceiptId}",
            "reader");
        using HttpResponseMessage replay = await PostReplayAsync(
            client,
            record.DecisionReceiptId,
            await GetAntiforgeryTokenAsync(client, "reader"),
            disabledConstraints: [],
            candidatePolicy: false,
            role: "reader");
        using HttpResponseMessage withoutToken = await PostReplayAsync(
            client,
            record.DecisionReceiptId,
            antiforgeryToken: null,
            disabledConstraints: [],
            candidatePolicy: false,
            role: _administratorRole);

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, find.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, withoutToken.StatusCode);
    }

    /// <summary>
    /// Verifies that the replay service, routes, and panel are absent outside Development even when the switch is true.
    /// </summary>
    /// <param name="environmentName">The host environment.</param>
    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Feature_OutsideDevelopment_IsNotRegisteredOrMapped(string environmentName)
    {
        using WebApplicationFactory<Program> factory = ApplicationWebApplicationFactory
            .CreateAllowingAnonymousAccess(new Dictionary<string, string?>
            {
                [$"{DecisionReplayOptions.SectionName}:Enabled"] = "true",
                ["ReferenceApplication:AuditCompletionOutbox:Enabled"] = "false"
            }, environmentName)
            .WithWebHostBuilder(static builder => builder.UseSetting($"{DecisionReplayOptions.SectionName}:Enabled", "true"));

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        Assert.Null(scope.ServiceProvider.GetService<IDecisionReplayService>());
        using HttpClient client = factory.CreateHttpsClient();
        using HttpResponseMessage list = await client.GetAsync(
            DecisionReplayServiceExtensions.DecisionsPath,
            TestContext.Current.CancellationToken);
        using HttpResponseMessage replay = await client.PostAsync(
            $"{DecisionReplayServiceExtensions.DecisionsPath}/receipt/replay",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
    }

    /// <summary>
    /// Verifies that the explorer shows the Decision replay tab and output only to an administrator in Development.
    /// </summary>
    [Fact]
    public async Task Explorer_ShowsReplayOnlyToAdministrators()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: false);
        using HttpClient client = factory.CreateHttpsClient();

        string administratorPage = await GetExplorerAsync(client, _administratorRole);
        string readerPage = await GetExplorerAsync(client, "reader");

        Assert.Contains("data-decision-replay=\"true\"", administratorPage, StringComparison.Ordinal);
        Assert.Contains("aria-controls=\"explorer-panel-replay\"", administratorPage, StringComparison.Ordinal);
        Assert.Contains("data-output-view=\"replay\"", administratorPage, StringComparison.Ordinal);
        Assert.Contains("id=\"decision-replay-form\"", administratorPage, StringComparison.Ordinal);
        Assert.Contains(
            $"data-constraint-name=\"{AdministratorRoleConstraint.ConstraintName}\"",
            administratorPage,
            StringComparison.Ordinal);
        Assert.Contains("data-decision-replay=\"false\"", readerPage, StringComparison.Ordinal);
        Assert.DoesNotContain("explorer-panel-replay", readerPage, StringComparison.Ordinal);
        Assert.DoesNotContain("data-output-view=\"replay\"", readerPage, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _database.Dispose();
    }

    private Task<WebApplicationFactory<Program>> CreateFactoryAsync(bool tokensEnabled)
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
            ["ReferenceApplication:Authorization:PermissionClaimType"] = ApplicationClaimTypes.Permission,
            ["ReferenceApplication:Authorization:AdministratorRoles:0"] = _administratorRole,
            ["ReferenceApplication:AuditCompletionOutbox:Enabled"] = "false"
        };

        // Feature switches are read during service registration, before test configuration sources are applied, so
        // they are pinned as host settings. That also keeps local appsettings.Development.json values and user secrets
        // from changing what these tests exercise.
        return _database.ApplyAsync(ApplicationWebApplicationFactory
            .CreateAllowingAnonymousAccess(configuration, Environments.Development)
            .WithWebHostBuilder(builder => ApplyHostSettings(builder, tokensEnabled)
                .ConfigureServices(static services => services
                    .AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                        _authenticationScheme,
                        static _ => { }))));
    }

    private static IWebHostBuilder ApplyHostSettings(IWebHostBuilder builder, bool tokensEnabled)
    {
        builder
            .UseSetting($"{DecisionReplayOptions.SectionName}:Enabled", "true")
            .UseSetting("ReferenceApplication:PolicySimulator:Enabled", "false")
            .UseSetting("ReferenceApplication:DemoIdentity:Enabled", "false")
            .UseSetting("ReferenceApplication:GovernanceLoadDemonstration:Enabled", "false")
            .UseSetting($"{CapabilityTokenOptions.SectionName}:Enabled", tokensEnabled ? "true" : "false")
            .UseSetting($"{CapabilityTokenOptions.SectionName}:LifetimeSeconds", 120.ToString(CultureInfo.InvariantCulture));

        return tokensEnabled
            ? builder
                .UseSetting($"{CapabilityTokenOptions.SectionName}:SigningKeyPem", _signingKey.Value.PrivateKeyPem)
                .UseSetting($"{CapabilityTokenOptions.SectionName}:VerificationKeys:0:KeyVersion", "1")
                .UseSetting($"{CapabilityTokenOptions.SectionName}:VerificationKeys:0:PublicKeyPem", _signingKey.Value.PublicKeyPem)
            : builder;
    }

    private static async Task<AuditLedgerRecord> ReadSingleRecordAsync(WebApplicationFactory<Program> factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        AuditLedgerRecordEntity persisted = await dbContext.AsiBackboneAuditLedgerRecords
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);

        return Assert.IsType<AuditLedgerRecord>(await new EfCoreAuditLedgerStore(dbContext)
            .FindByRecordIdAsync(persisted.RecordId, TestContext.Current.CancellationToken));
    }

    private static async Task<AuditLedgerRecord> AppendRecordUnderOlderPolicyAsync(
        WebApplicationFactory<Program> factory,
        AuditLedgerRecord template)
    {
        // A decision for the same endpoint and actor, as a previous policy version would have recorded it.
        var receipt = DecisionReceipt.FromDecision(
            GovernanceActorContext.Human(template.ActorId, template.ActorDisplayName),
            template.OperationName,
            GovernanceDecision.Allow(
                correlationId: "decision-replay-older-policy",
                policyVersion: "0.9.0",
                policyHash: $"sha256:{new string('0', 64)}"),
            metadata: template.Metadata,
            decisionStage: template.DecisionStage);
        AuditLedgerRecord record = AuditLedgerRecordHashing.CreateHashed(receipt);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        OperationResult<AuditLedgerRecord> result = await new EfCoreAuditLedgerStore(
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>())
            .AppendAsync(record, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
        return record;
    }

    private static async Task<JsonDocument> ReplayAsync(
        HttpClient client,
        string decisionReceiptId,
        IReadOnlyList<string>? disabledConstraints = null,
        bool candidatePolicy = false)
    {
        using HttpResponseMessage response = await PostReplayAsync(
            client,
            decisionReceiptId,
            await GetAntiforgeryTokenAsync(client, _administratorRole),
            disabledConstraints ?? [],
            candidatePolicy,
            _administratorRole);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"The replay returned {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    private static async Task<HttpResponseMessage> PostReplayAsync(
        HttpClient client,
        string decisionReceiptId,
        string? antiforgeryToken,
        IReadOnlyList<string> disabledConstraints,
        bool candidatePolicy,
        string role)
    {
        List<KeyValuePair<string, string>> fields = [];
        if (antiforgeryToken is not null)
        {
            fields.Add(new(_antiforgeryField, antiforgeryToken));
        }

        if (candidatePolicy)
        {
            fields.Add(new("candidatePolicy", "true"));
        }

        fields.AddRange(disabledConstraints.Select(static name => new KeyValuePair<string, string>("disabledConstraints", name)));

        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Post,
            $"{DecisionReplayServiceExtensions.DecisionsPath}/{Uri.EscapeDataString(decisionReceiptId)}/replay",
            role,
            permission: null);
        request.Headers.Accept.ParseAdd("application/json");
        request.Content = new FormUrlEncodedContent(fields);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string role)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, path, role, permission: null);
        request.Headers.Accept.ParseAdd("application/json");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpStatusCode> RunOperationAsync(
        HttpClient client,
        string operation,
        string? role,
        string? permission)
    {
        using HttpResponseMessage response = await PostOperationAsync(client, operation, role, permission);
        return response.StatusCode;
    }

    private static async Task<HttpResponseMessage> PostOperationAsync(
        HttpClient client,
        string operation,
        string? role,
        string? permission)
    {
        Dictionary<string, string> fields = new(StringComparer.Ordinal)
        {
            [_antiforgeryField] = await GetAntiforgeryTokenAsync(client, role, permission),
            [operation == "records" ? "Title" : "Note"] = _payload
        };

        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Post,
            $"/api/v1/governed-operations/{operation}",
            role,
            permission);
        request.Headers.Accept.ParseAdd("application/problem+json");
        request.Content = new FormUrlEncodedContent(fields);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string> GetExplorerAsync(HttpClient client, string role)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, "/governance-explorer", role, permission: null);
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string? role, string? permission = null)
    {
        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Get,
            "/test/authentication/antiforgery-token",
            role,
            permission);
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string? role, string? permission)
    {
        HttpRequestMessage request = new(method, path);
        request.Headers.Add(_userHeader, _user);

        if (role is not null)
        {
            request.Headers.Add(_roleHeader, role);
        }

        if (permission is not null)
        {
            request.Headers.Add(_permissionHeader, permission);
        }

        return request;
    }

    private static async Task<EvidenceCounts> CountEvidenceAsync(WebApplicationFactory<Program> factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        return new EvidenceCounts(
            Ledger: await dbContext.AsiBackboneAuditLedgerRecords.CountAsync(cancellationToken),
            Lifecycle: await dbContext.AsiBackboneDecisionReceiptLifecycleEvents.CountAsync(cancellationToken),
            GovernanceOutbox: await dbContext.Set<GovernanceOutboxEntryEntity>().CountAsync(cancellationToken),
            CompletionOutbox: await dbContext.ApplicationAuditCompletionOutboxEntries.CountAsync(cancellationToken),
            MutationAudit: await dbContext.AuditRecords.CountAsync(cancellationToken),
            ApplicationRecords: await dbContext.GovernedOperationRecords.CountAsync(cancellationToken),
            CapabilityUses: await dbContext.Set<CapabilityGrantUseEntity>().CountAsync(cancellationToken));
    }

    private static string[] ReadStrings(JsonElement array)
    {
        return [.. array.EnumerateArray().Select(static value => value.GetString()!)];
    }

    private sealed record EvidenceCounts(
        int Ledger,
        int Lifecycle,
        int GovernanceOutbox,
        int CompletionOutbox,
        int MutationAudit,
        int ApplicationRecords,
        int CapabilityUses);

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
                new(ApplicationClaimTypes.Name, "Decision replay test actor")
            ];
            AddClaimFromHeader(claims, _roleHeader, ApplicationClaimTypes.Role);
            AddClaimFromHeader(claims, _permissionHeader, ApplicationClaimTypes.Permission);

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
