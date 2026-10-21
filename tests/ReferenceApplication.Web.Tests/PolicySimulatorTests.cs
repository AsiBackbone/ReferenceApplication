using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using AsiBackbone.AspNetCore.CapabilityGrants;
using AsiBackbone.Core.Audit;
using AsiBackbone.Core.Constraints;
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
using ReferenceApplication.Web.Governance.Simulation;
using ReferenceApplication.Web.Tests.Extensions;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies the Development-only policy simulator: that it reports what the real endpoints persist, marks a modified
/// policy, persists nothing, and is reachable only by administrators in Development with an antiforgery token.
/// </summary>
public sealed class PolicySimulatorTests : IDisposable
{
    private const string _authenticationScheme = "PolicySimulatorTestAuthentication";
    private const string _userHeader = "X-Test-User";
    private const string _roleHeader = "X-Test-Role";
    private const string _permissionHeader = "X-Test-Permission";
    private const string _user = "simulator-user";
    private const string _administratorRole = "administrator";
    private const string _antiforgeryField = "__RequestVerificationToken";
    private const string _scope = ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope;
    private const string _payload = "Policy simulator record";
    private const int _lifetimeSeconds = 120;

    // Generated for the test run, so no signing key is checked in.
    private static readonly Lazy<(string PrivateKeyPem, string PublicKeyPem)> _signingKey = new(static () =>
    {
        using var rsa = RSA.Create(2048);
        return (rsa.ExportPkcs8PrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    });

    private readonly TestSqliteDatabase _database = TestSqliteDatabase.Create();

    /// <summary>
    /// Verifies that, for each sample operation and simulated actor, the simulated receipt carries the decision and
    /// reason codes the real endpoint then persists for the same inputs, and that the simulated endpoint result
    /// matches the real response.
    /// </summary>
    /// <param name="operation">The governed operation.</param>
    /// <param name="administrator">Whether the actor holds the administrator role.</param>
    /// <param name="capability">Whether the actor holds the consequential-execution capability.</param>
    [Theory]
    [InlineData("administrator", true, false)]
    [InlineData("administrator", false, true)]
    [InlineData("capability", false, true)]
    [InlineData("capability", true, false)]
    [InlineData("consequential", true, true)]
    [InlineData("consequential", false, true)]
    [InlineData("consequential", true, false)]
    [InlineData("records", true, false)]
    [InlineData("records", false, false)]
    public async Task Simulation_MatchesWhatTheRealEndpointPersists(string operation, bool administrator, bool capability)
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: false);
        using HttpClient client = factory.CreateHttpsClient();

        using JsonDocument simulation = await SimulateAsync(
            client,
            operation,
            administrator,
            capability,
            callerPermission: capability ? _scope : null);
        JsonElement result = simulation.RootElement;
        JsonElement receipt = result.GetProperty("receipt");
        Assert.Equal(0, await CountPersistedEvidenceAsync(factory));

        using HttpResponseMessage real = await PostOperationAsync(
            client,
            operation,
            role: administrator ? _administratorRole : null,
            permission: capability ? _scope : null);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        AuditLedgerRecordEntity persisted = await dbContext.AsiBackboneAuditLedgerRecords
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        AuditLedgerRecord record = Assert.IsType<AuditLedgerRecord>(await new EfCoreAuditLedgerStore(dbContext)
            .FindByRecordIdAsync(persisted.RecordId, TestContext.Current.CancellationToken));

        Assert.Equal(record.OperationName, receipt.GetProperty("operationName").GetString());
        Assert.Equal(record.Outcome, receipt.GetProperty("outcome").GetString());
        Assert.Equal(record.ReasonCodes, ReadStrings(receipt.GetProperty("reasonCodes")));
        Assert.Equal(record.ActorId, receipt.GetProperty("actorId").GetString());
        Assert.Equal(record.PolicyVersion, receipt.GetProperty("policyVersion").GetString());
        Assert.Equal(record.PolicyHash, receipt.GetProperty("policyHash").GetString());

        JsonElement endpointResult = result.GetProperty("endpointResult");
        if (endpointResult.GetProperty("kind").GetString() == "allowed")
        {
            Assert.True(real.IsSuccessStatusCode, $"The real endpoint returned {(int)real.StatusCode}.");
        }
        else
        {
            Assert.Equal(endpointResult.GetProperty("statusCode").GetInt32(), (int)real.StatusCode);
        }

        // The simulator caller is always an administrator holding the simulated capability, so the inputs can be sent
        // for real exactly when the simulated actor is an administrator too.
        Assert.Equal(administrator, result.GetProperty("sendForReal").GetProperty("available").GetBoolean());
        Assert.False(result.GetProperty("simulatedWithModifiedPolicy").GetBoolean());
    }

    /// <summary>
    /// Verifies the staged output: each constraint in evaluation order, the decision-policy step that turns an
    /// allowed consequential request into an acknowledgment-required one, and the claim-based capability step.
    /// </summary>
    [Fact]
    public async Task Simulation_ReportsEachStageInEvaluationOrder()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: false);
        using HttpClient client = factory.CreateHttpsClient();

        using JsonDocument simulation = await SimulateAsync(
            client,
            "consequential",
            administrator: true,
            capability: true,
            callerPermission: _scope);
        JsonElement result = simulation.RootElement;

        string[] expectedNames = [AdministratorRoleConstraint.ConstraintName, CapabilityScopeDeclarationConstraint.ConstraintName];
        string[] constraintNames = [.. result.GetProperty("constraints").EnumerateArray()
            .Select(static constraint => constraint.GetProperty("name").GetString()!)];
        Assert.Equal(expectedNames, constraintNames);
        Assert.All(result.GetProperty("constraints").EnumerateArray(), static constraint =>
        {
            Assert.True(constraint.GetProperty("evaluated").GetBoolean());
            Assert.Equal(ConstraintEvaluationOutcome.Allowed.ToString(), constraint.GetProperty("outcome").GetString());
        });

        JsonElement decisionPolicy = result.GetProperty("decisionPolicy");
        Assert.True(decisionPolicy.GetProperty("changed").GetBoolean());
        Assert.Contains(
            ConsequentialActionDecisionPolicy.AcknowledgmentReasonCode,
            ReadStrings(decisionPolicy.GetProperty("reasonCodes")));

        JsonElement capabilityStage = result.GetProperty("capability");
        Assert.True(capabilityStage.GetProperty("applies").GetBoolean());
        Assert.Equal("claims", capabilityStage.GetProperty("model").GetString());
        Assert.True(capabilityStage.GetProperty("evaluated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, capabilityStage.GetProperty("token").ValueKind);

        JsonElement endpointResult = result.GetProperty("endpointResult");
        Assert.Equal("acknowledgment-challenge", endpointResult.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrWhiteSpace(endpointResult.GetProperty("requiredAcknowledgmentCode").GetString()));
    }

    /// <summary>
    /// Verifies that leaving a constraint out changes the result as expected and marks it as simulated with a
    /// modified policy, which cannot be sent for real.
    /// </summary>
    [Fact]
    public async Task Simulation_WithConstraintLeftOut_ChangesResultAndMarksModifiedPolicy()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: false);
        using HttpClient client = factory.CreateHttpsClient();

        using JsonDocument allowed = await SimulateAsync(client, "administrator", administrator: true, capability: false);
        using JsonDocument denied = await SimulateAsync(client, "administrator", administrator: false, capability: false);
        using JsonDocument modified = await SimulateAsync(
            client,
            "administrator",
            administrator: false,
            capability: false,
            disabledConstraints: [AdministratorRoleConstraint.ConstraintName]);

        JsonElement deniedReceipt = denied.RootElement.GetProperty("receipt");
        JsonElement modifiedReceipt = modified.RootElement.GetProperty("receipt");
        Assert.Contains(
            AdministratorRoleConstraint.DeniedReasonCode,
            ReadStrings(deniedReceipt.GetProperty("reasonCodes")));
        Assert.NotEqual(
            deniedReceipt.GetProperty("outcome").GetString(),
            modifiedReceipt.GetProperty("outcome").GetString());
        Assert.Equal(
            allowed.RootElement.GetProperty("receipt").GetProperty("outcome").GetString(),
            modifiedReceipt.GetProperty("outcome").GetString());
        Assert.Empty(ReadStrings(modifiedReceipt.GetProperty("reasonCodes")));

        JsonElement result = modified.RootElement;
        Assert.True(result.GetProperty("simulatedWithModifiedPolicy").GetBoolean());
        Assert.Equal(
            AdministratorRoleConstraint.ConstraintName,
            Assert.Single(ReadStrings(result.GetProperty("disabledConstraints"))));
        JsonElement administratorStage = result.GetProperty("constraints").EnumerateArray().Single(static constraint =>
            constraint.GetProperty("name").GetString() == AdministratorRoleConstraint.ConstraintName);
        Assert.True(administratorStage.GetProperty("disabled").GetBoolean());
        Assert.False(administratorStage.GetProperty("evaluated").GetBoolean());
        Assert.False(result.GetProperty("sendForReal").GetProperty("available").GetBoolean());
        Assert.False(denied.RootElement.GetProperty("simulatedWithModifiedPolicy").GetBoolean());
    }

    /// <summary>
    /// Verifies that simulated evaluations, including modified-policy ones, leave the ledger, outbox, lifecycle,
    /// mutation-audit, and capability-use tables unchanged.
    /// </summary>
    [Fact]
    public async Task Simulation_PersistsNothing()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: false);
        using HttpClient client = factory.CreateHttpsClient();

        foreach (string operation in PolicySimulator.OperationKeys)
        {
            using JsonDocument full = await SimulateAsync(client, operation, administrator: true, capability: true, callerPermission: _scope);
            using JsonDocument modified = await SimulateAsync(
                client,
                operation,
                administrator: false,
                capability: false,
                disabledConstraints: [CapabilityScopeDeclarationConstraint.ConstraintName]);
            Assert.Equal(JsonValueKind.Object, full.RootElement.GetProperty("receipt").ValueKind);
            Assert.Equal(JsonValueKind.Object, modified.RootElement.GetProperty("receipt").ValueKind);
        }

        Assert.Equal(0, await CountPersistedEvidenceAsync(factory));
    }

    /// <summary>
    /// Verifies that with signed capability tokens enabled the capability stage shows the token issuance would sign,
    /// with its claims, payload binding, and expiry but never the signing key, without consuming a use.
    /// </summary>
    [Fact]
    public async Task Simulation_WithTokensEnabled_PreviewsTheTokenWithoutTheKeyOrConsumingAUse()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: true);
        using HttpClient client = factory.CreateHttpsClient();

        using JsonDocument entitled = await SimulateAsync(
            client,
            "capability",
            administrator: true,
            capability: true,
            callerPermission: _scope);
        using JsonDocument notEntitled = await SimulateAsync(client, "capability", administrator: true, capability: false);

        string json = entitled.RootElement.GetRawText();
        JsonElement capabilityStage = entitled.RootElement.GetProperty("capability");
        JsonElement token = capabilityStage.GetProperty("token");
        Assert.Equal("signed-token", capabilityStage.GetProperty("model").GetString());
        Assert.True(token.GetProperty("wouldBeIssued").GetBoolean());
        Assert.Equal(_user, token.GetProperty("subjectId").GetString());
        Assert.Equal(1, token.GetProperty("maxUseCount").GetInt32());
        Assert.StartsWith(
            FormFieldCapabilityGrantRequestBindingProvider.BindingPrefix,
            token.GetProperty("resourceBinding").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(
            TimeSpan.FromSeconds(_lifetimeSeconds),
            token.GetProperty("expiresUtc").GetDateTimeOffset() - token.GetProperty("issuedUtc").GetDateTimeOffset());
        Assert.Equal(
            token.GetProperty("tokenId").GetString(),
            entitled.RootElement.GetProperty("receipt").GetProperty("capabilityGrantId").GetString());
        Assert.DoesNotContain("PRIVATE KEY", json, StringComparison.Ordinal);
        Assert.DoesNotContain(PemBody(_signingKey.Value.PrivateKeyPem), json, StringComparison.Ordinal);
        Assert.False(entitled.RootElement.GetProperty("sendForReal").GetProperty("available").GetBoolean());

        JsonElement deniedStage = notEntitled.RootElement.GetProperty("capability");
        Assert.False(deniedStage.GetProperty("token").GetProperty("wouldBeIssued").GetBoolean());
        Assert.Contains(
            SignedCapabilityGrantReasonCodes.Missing,
            ReadStrings(notEntitled.RootElement.GetProperty("receipt").GetProperty("reasonCodes")));

        Assert.Equal(0, await CountPersistedEvidenceAsync(factory));
    }

    /// <summary>
    /// Verifies that the simulator rejects a consequential simulation without the acknowledgment flag, an unknown
    /// operation, and an unknown constraint.
    /// </summary>
    /// <param name="operation">The governed operation.</param>
    /// <param name="acknowledged">Whether the acknowledgment flag is set.</param>
    /// <param name="disabledConstraint">A constraint to leave out, or <see langword="null" />.</param>
    [Theory]
    [InlineData("consequential", false, null)]
    [InlineData("unknown", true, null)]
    [InlineData("administrator", true, "referenceapplication.unknown")]
    public async Task Simulation_InvalidInputs_AreRejected(string operation, bool acknowledged, string? disabledConstraint)
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: false);
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostSimulationAsync(
            client,
            CreateSimulationForm(
                await GetAntiforgeryTokenAsync(client, _administratorRole, permission: null),
                operation,
                administrator: true,
                capability: false,
                acknowledged,
                disabledConstraint is null ? [] : [disabledConstraint]),
            role: _administratorRole,
            permission: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Verifies that a non-administrator is rejected.
    /// </summary>
    [Fact]
    public async Task Evaluate_NonAdministrator_IsForbidden()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: false);
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostSimulationAsync(
            client,
            CreateSimulationForm(
                await GetAntiforgeryTokenAsync(client, "reader", permission: null),
                "administrator",
                administrator: true,
                capability: false,
                acknowledged: false,
                []),
            role: "reader",
            permission: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Verifies that a request without an antiforgery token is rejected.
    /// </summary>
    [Fact]
    public async Task Evaluate_WithoutAntiforgeryToken_ReturnsBadRequest()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: false);
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostSimulationAsync(
            client,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["operation"] = "administrator",
                ["administrator"] = "true"
            }),
            role: _administratorRole,
            permission: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Verifies that the simulator service, route, and panel are absent outside Development even when the switch is
    /// true.
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
                [$"{PolicySimulatorOptions.SectionName}:Enabled"] = "true",
                ["ReferenceApplication:AuditCompletionOutbox:Enabled"] = "false"
            }, environmentName)
            .WithWebHostBuilder(builder => builder.UseSetting($"{PolicySimulatorOptions.SectionName}:Enabled", "true"));

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        Assert.Null(scope.ServiceProvider.GetService<IPolicySimulator>());
        using HttpClient client = factory.CreateHttpsClient();
        using HttpResponseMessage response = await client.PostAsync(
            PolicySimulatorServiceExtensions.EvaluatePath,
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Verifies that the explorer shows the panel, with a toggle for each registered constraint, to an administrator in
    /// Development, and hides it from other actors.
    /// </summary>
    [Fact]
    public async Task Explorer_ShowsPanelOnlyToAdministrators()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: false);
        using HttpClient client = factory.CreateHttpsClient();

        string administratorPage = await GetExplorerAsync(client, _administratorRole);
        string readerPage = await GetExplorerAsync(client, "reader");

        Assert.Contains("data-policy-simulator=\"true\"", administratorPage, StringComparison.Ordinal);
        Assert.Contains("id=\"policy-simulator-form\"", administratorPage, StringComparison.Ordinal);
        Assert.Contains("aria-controls=\"explorer-panel-simulator\"", administratorPage, StringComparison.Ordinal);
        Assert.Contains("data-output-view=\"simulator\"", administratorPage, StringComparison.Ordinal);
        Assert.Contains(
            $"data-constraint-name=\"{AdministratorRoleConstraint.ConstraintName}\"",
            administratorPage,
            StringComparison.Ordinal);
        Assert.Contains(
            $"data-constraint-name=\"{CapabilityScopeDeclarationConstraint.ConstraintName}\"",
            administratorPage,
            StringComparison.Ordinal);
        Assert.Contains("data-policy-simulator=\"false\"", readerPage, StringComparison.Ordinal);
        Assert.DoesNotContain("policy-simulator-form", readerPage, StringComparison.Ordinal);
        Assert.DoesNotContain("explorer-tab-simulator", readerPage, StringComparison.Ordinal);
        Assert.DoesNotContain("data-output-view=\"simulator\"", readerPage, StringComparison.Ordinal);
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
                .ConfigureServices(services => services
                    .AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                        _authenticationScheme,
                        _ => { }))));
    }

    private static IWebHostBuilder ApplyHostSettings(IWebHostBuilder builder, bool tokensEnabled)
    {
        builder
            .UseSetting($"{PolicySimulatorOptions.SectionName}:Enabled", "true")
            .UseSetting("ReferenceApplication:DemoIdentity:Enabled", "false")
            .UseSetting("ReferenceApplication:GovernanceLoadDemonstration:Enabled", "false")
            .UseSetting($"{CapabilityTokenOptions.SectionName}:Enabled", tokensEnabled ? "true" : "false")
            .UseSetting(
                $"{CapabilityTokenOptions.SectionName}:LifetimeSeconds",
                _lifetimeSeconds.ToString(CultureInfo.InvariantCulture));

        return tokensEnabled
            ? builder
                .UseSetting($"{CapabilityTokenOptions.SectionName}:SigningKeyPem", _signingKey.Value.PrivateKeyPem)
                .UseSetting($"{CapabilityTokenOptions.SectionName}:VerificationKeys:0:KeyVersion", "1")
                .UseSetting($"{CapabilityTokenOptions.SectionName}:VerificationKeys:0:PublicKeyPem", _signingKey.Value.PublicKeyPem)
            : builder;
    }

    private static async Task<JsonDocument> SimulateAsync(
        HttpClient client,
        string operation,
        bool administrator,
        bool capability,
        string? callerPermission = null,
        IReadOnlyList<string>? disabledConstraints = null)
    {
        using HttpResponseMessage response = await PostSimulationAsync(
            client,
            CreateSimulationForm(
                await GetAntiforgeryTokenAsync(client, _administratorRole, callerPermission),
                operation,
                administrator,
                capability,
                acknowledged: true,
                disabledConstraints ?? []),
            role: _administratorRole,
            permission: callerPermission);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"The simulation returned {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    private static FormUrlEncodedContent CreateSimulationForm(
        string antiforgeryToken,
        string operation,
        bool administrator,
        bool capability,
        bool acknowledged,
        IReadOnlyList<string> disabledConstraints)
    {
        List<KeyValuePair<string, string>> fields =
        [
            new(_antiforgeryField, antiforgeryToken),
            new("operation", operation),
            new("payload", _payload)
        ];

        if (administrator)
        {
            fields.Add(new("administrator", "true"));
        }

        if (capability)
        {
            fields.Add(new("consequentialCapability", "true"));
        }

        if (acknowledged)
        {
            fields.Add(new("acknowledged", "true"));
        }

        fields.AddRange(disabledConstraints.Select(static name => new KeyValuePair<string, string>("disabledConstraints", name)));
        return new FormUrlEncodedContent(fields);
    }

    private static async Task<HttpResponseMessage> PostSimulationAsync(
        HttpClient client,
        HttpContent content,
        string? role,
        string? permission)
    {
        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Post,
            PolicySimulatorServiceExtensions.EvaluatePath,
            role,
            permission);
        request.Headers.Accept.ParseAdd("application/json");
        request.Content = content;
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
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

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string? role, string? permission)
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

    private static async Task<int> CountPersistedEvidenceAsync(WebApplicationFactory<Program> factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        return await dbContext.AsiBackboneAuditLedgerRecords.CountAsync(cancellationToken)
            + await dbContext.AsiBackboneDecisionReceiptLifecycleEvents.CountAsync(cancellationToken)
            + await dbContext.Set<GovernanceOutboxEntryEntity>().CountAsync(cancellationToken)
            + await dbContext.ApplicationAuditCompletionOutboxEntries.CountAsync(cancellationToken)
            + await dbContext.AuditRecords.CountAsync(cancellationToken)
            + await dbContext.Set<CapabilityGrantUseEntity>().CountAsync(cancellationToken);
    }

    private static string[] ReadStrings(JsonElement array)
    {
        return [.. array.EnumerateArray().Select(static value => value.GetString()!)];
    }

    private static string PemBody(string pem)
    {
        string[] lines = pem.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 2 ? lines[1] : pem;
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
                new(ApplicationClaimTypes.Name, "Policy simulator test actor")
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
