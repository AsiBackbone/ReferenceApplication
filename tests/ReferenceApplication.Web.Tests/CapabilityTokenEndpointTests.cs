using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using AsiBackbone.AspNetCore.CapabilityGrants;
using AsiBackbone.Core.Audit;
using AsiBackbone.Core.CapabilityGrants;
using AsiBackbone.EntityFrameworkCore.Audit;
using AsiBackbone.EntityFrameworkCore.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ReferenceApplication.Infrastructure.Data;
using ReferenceApplication.Web.Authentication.Claims;
using ReferenceApplication.Web.Controllers.Api;
using ReferenceApplication.Web.Governance;
using ReferenceApplication.Web.Tests.Extensions;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies signed capability tokens end to end through the web host, AsiBackbone's signed capability grant
/// validator, and the audit ledger.
/// </summary>
/// <remarks>
/// Each denial is asserted with the reason code AsiBackbone documents for it. Exhaustive per-category coverage lives
/// in AsiBackbone itself; these tests show each case through the sample endpoints.
/// </remarks>
public sealed class CapabilityTokenEndpointTests : IDisposable
{
    private const string _testAuthenticationScheme = "CapabilityTokenTestAuthentication";
    private const string _testUserHeaderName = "X-Test-User";
    private const string _testRoleHeaderName = "X-Test-Role";
    private const string _testPermissionHeaderName = "X-Test-Permission";
    private const string _antiforgeryFormFieldName = "__RequestVerificationToken";
    private const string _tokensPath = "/api/v1/capability-tokens";
    private const string _operationsPath = "/api/v1/governed-operations";
    private const string _scope = ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope;
    private const int _lifetimeSeconds = 120;

    // Generated for the test run, so no signing key is checked in. The public half is the only key verification sees.
    private static readonly Lazy<(string PrivateKeyPem, string PublicKeyPem)> _signingKey = new(static () =>
    {
        using var rsa = RSA.Create(2048);
        return (rsa.ExportPkcs8PrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    });

    private readonly TestSqliteDatabase _database = TestSqliteDatabase.Create();
    private readonly MutableTimeProvider _clock = new(DateTimeOffset.UtcNow);

    /// <summary>
    /// Verifies that an issued token authorizes exactly the request it was issued for, that its identifier is stored in
    /// the persisted decision evidence under a verifying record hash, and that replaying it is denied.
    /// </summary>
    [Fact]
    public async Task IssuedToken_AuthorizesItsRequestOnce_AndIsRecordedInDecisionEvidence()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: true);
        using HttpClient client = factory.CreateHttpsClient();

        CapabilityTokenResponse issued = await IssueAsync(client, "capability", permission: _scope);

        using HttpResponseMessage first = await PostOperationAsync(client, "capability", issued.Token);
        using HttpResponseMessage replay = await PostOperationAsync(client, "capability", issued.Token);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.Equal(SignedCapabilityGrantOptions.DefaultTokenHeaderName, issued.HeaderName);
        Assert.Equal(_scope, Assert.Single(issued.Scopes));
        Assert.StartsWith(FormFieldCapabilityGrantRequestBindingProvider.BindingPrefix, issued.ResourceBinding, StringComparison.Ordinal);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        List<AuditLedgerRecordEntity> decisions = await dbContext.AsiBackboneAuditLedgerRecords
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, decisions.Count);

        AuditLedgerRecordEntity allowed = Assert.Single(
            decisions,
            static decision => decision.CapabilityTokenId is not null);
        Assert.Equal(issued.TokenId, allowed.CapabilityTokenId);
        AuditLedgerRecord allowedRecord = await FindRecordAsync(dbContext, allowed.RecordId);
        Assert.Equal(issued.OperationName, allowedRecord.OperationName);
        Assert.Equal(allowedRecord.RecordHash, AuditLedgerRecordHashing.ComputeHash(allowedRecord).HashValue);

        AuditLedgerRecordEntity denied = Assert.Single(
            decisions,
            static decision => decision.CapabilityTokenId is null);
        AuditLedgerRecord deniedRecord = await FindRecordAsync(dbContext, denied.RecordId);
        Assert.Contains(SignedCapabilityGrantReasonCodes.UseLimitExceeded, deniedRecord.ReasonCodes);
    }

    /// <summary>
    /// Verifies that two application instances sharing the database each verify a token from configured public keys
    /// alone, and together accept a single-use token exactly once.
    /// </summary>
    [Fact]
    public async Task IssuedToken_IsConsumedOnceAcrossInstancesSharingTheDatabase()
    {
        using WebApplicationFactory<Program> issuingInstance = await CreateFactoryAsync(tokensEnabled: true);
        using WebApplicationFactory<Program> otherInstance = await CreateFactoryAsync(tokensEnabled: true);
        using HttpClient issuingClient = issuingInstance.CreateHttpsClient();
        using HttpClient otherClient = otherInstance.CreateHttpsClient();

        CapabilityTokenResponse issued = await IssueAsync(issuingClient, "capability", permission: _scope);

        using HttpResponseMessage accepted = await PostOperationAsync(otherClient, "capability", issued.Token);
        using HttpResponseMessage replayed = await PostOperationAsync(issuingClient, "capability", issued.Token);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, replayed.StatusCode);

        await using AsyncServiceScope scope = issuingInstance.Services.CreateAsyncScope();
        ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        CapabilityGrantUseEntity use = await dbContext.Set<CapabilityGrantUseEntity>()
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(issued.TokenId, use.TokenId);
        Assert.Equal(1, use.UseCount);

        List<AuditLedgerRecordEntity> decisions = await dbContext.AsiBackboneAuditLedgerRecords
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, decisions.Count);
        Assert.Equal(issued.TokenId, Assert.Single(decisions, static decision => decision.CapabilityTokenId is not null).CapabilityTokenId);
        AuditLedgerRecordEntity denied = Assert.Single(decisions, static decision => decision.CapabilityTokenId is null);
        Assert.Contains(
            SignedCapabilityGrantReasonCodes.UseLimitExceeded,
            (await FindRecordAsync(dbContext, denied.RecordId)).ReasonCodes);
    }

    /// <summary>
    /// Verifies that the scheduled purge keeps a consumed grant through expiry plus the retention grace period, and
    /// removes it afterwards.
    /// </summary>
    [Fact]
    public async Task UseStorePurge_RemovesConsumedGrantOnlyAfterItsRetentionHorizon()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: true);
        using HttpClient client = factory.CreateHttpsClient();
        CapabilityGrantUsePurgeService purge = Assert.Single(
            factory.Services.GetServices<IHostedService>().OfType<CapabilityGrantUsePurgeService>());

        CapabilityTokenResponse issued = await IssueAsync(client, "capability", permission: _scope);
        using HttpResponseMessage response = await PostOperationAsync(client, "capability", issued.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        _clock.Advance(TimeSpan.FromSeconds(_lifetimeSeconds));
        int purgedWithinRetention = await purge.PurgeOnceAsync(TestContext.Current.CancellationToken);

        _clock.Advance(TimeSpan.FromSeconds(CapabilityTokenOptions.MaximumAllowedClockSkewSeconds + 1));
        int purgedAfterRetention = await purge.PurgeOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, purgedWithinRetention);
        Assert.Equal(1, purgedAfterRetention);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await dbContext.Set<CapabilityGrantUseEntity>().AnyAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies that the actor's permission claim alone no longer authorizes the operation while tokens are enabled.
    /// </summary>
    [Fact]
    public async Task CapabilityAction_TokensEnabledWithoutToken_IsDenied()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: true);
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostOperationAsync(client, "capability", token: null, permission: _scope);

        await AssertDeniedAsync(factory, response, SignedCapabilityGrantReasonCodes.Missing);
    }

    /// <summary>
    /// Verifies that a token edited after signing fails verification.
    /// </summary>
    [Fact]
    public async Task CapabilityAction_TamperedTokenField_IsDenied()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: true);
        using HttpClient client = factory.CreateHttpsClient();

        CapabilityTokenResponse issued = await IssueAsync(client, "capability", permission: _scope);
        string tampered = EditToken(issued.Token, document => document["grant"]!["maxUseCount"] = 50);

        using HttpResponseMessage response = await PostOperationAsync(client, "capability", tampered);

        await AssertDeniedAsync(factory, response, SignedCapabilityGrantReasonCodes.ProofInvalid);
    }

    /// <summary>
    /// Verifies that changing the request payload after issuance invalidates the token for that request.
    /// </summary>
    [Fact]
    public async Task CapabilityAction_DifferentRequestBinding_IsDenied()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: true);
        using HttpClient client = factory.CreateHttpsClient();

        CapabilityTokenResponse issued = await IssueAsync(
            client,
            "capability",
            permission: _scope,
            payload: new Dictionary<string, string>(StringComparer.Ordinal) { ["Note"] = "approved" });
        using HttpResponseMessage response = await PostOperationAsync(
            client,
            "capability",
            issued.Token,
            form: new Dictionary<string, string>(StringComparer.Ordinal) { ["Note"] = "tampered" });

        await AssertDeniedAsync(factory, response, SignedCapabilityGrantReasonCodes.ResourceMismatch);
    }

    /// <summary>
    /// Verifies that a token bound to the payload it was issued for authorizes that payload, with the antiforgery field
    /// left out of the binding.
    /// </summary>
    [Fact]
    public async Task CapabilityAction_MatchingRequestBinding_IsAllowed()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: true);
        using HttpClient client = factory.CreateHttpsClient();
        Dictionary<string, string> payload = new(StringComparer.Ordinal) { ["Note"] = "approved" };

        CapabilityTokenResponse issued = await IssueAsync(client, "capability", permission: _scope, payload: payload);
        using HttpResponseMessage response = await PostOperationAsync(client, "capability", issued.Token, form: payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Verifies that a token issued for another operation is denied.
    /// </summary>
    [Fact]
    public async Task CapabilityAction_TokenForAnotherOperation_IsDenied()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: true);
        using HttpClient client = factory.CreateHttpsClient();

        CapabilityTokenResponse reference = await IssueAsync(client, "capability", permission: _scope);
        string token = await IssueDirectAsync(factory, reference, operationName: "referenceapplication.other-operation");

        using HttpResponseMessage response = await PostOperationAsync(client, "capability", token);

        await AssertDeniedAsync(factory, response, SignedCapabilityGrantReasonCodes.OperationMismatch);
    }

    /// <summary>
    /// Verifies that a token without the scope the endpoint requires is denied.
    /// </summary>
    [Fact]
    public async Task CapabilityAction_TokenWithoutRequiredScope_IsDenied()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: true);
        using HttpClient client = factory.CreateHttpsClient();

        CapabilityTokenResponse reference = await IssueAsync(client, "capability", permission: _scope);
        string token = await IssueDirectAsync(factory, reference, scopes: ["referenceapplication.other.scope"]);

        using HttpResponseMessage response = await PostOperationAsync(client, "capability", token);

        await AssertDeniedAsync(factory, response, SignedCapabilityGrantReasonCodes.ScopeMissing);
    }

    /// <summary>
    /// Verifies that a token presented after it expires is denied.
    /// </summary>
    [Fact]
    public async Task CapabilityAction_ExpiredToken_IsDenied()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: true);
        using HttpClient client = factory.CreateHttpsClient();

        CapabilityTokenResponse issued = await IssueAsync(client, "capability", permission: _scope);
        _clock.Advance(TimeSpan.FromSeconds(_lifetimeSeconds + 1));

        using HttpResponseMessage response = await PostOperationAsync(client, "capability", issued.Token);

        await AssertDeniedAsync(factory, response, SignedCapabilityGrantReasonCodes.Expired);
    }

    /// <summary>
    /// Verifies that a token presented before its not-before time is denied rather than deferred.
    /// </summary>
    [Fact]
    public async Task CapabilityAction_NotYetValidToken_IsDenied()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: true);
        using HttpClient client = factory.CreateHttpsClient();

        CapabilityTokenResponse issued = await IssueAsync(client, "capability", permission: _scope);
        _clock.Advance(TimeSpan.FromSeconds(-30));

        using HttpResponseMessage response = await PostOperationAsync(client, "capability", issued.Token);

        await AssertDeniedAsync(factory, response, SignedCapabilityGrantReasonCodes.NotYetValid);
    }

    /// <summary>
    /// Verifies that a token presented by an actor other than the one it was issued to is denied.
    /// </summary>
    [Fact]
    public async Task CapabilityAction_TokenPresentedByAnotherActor_IsDenied()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: true);
        using HttpClient client = factory.CreateHttpsClient();

        CapabilityTokenResponse issued = await IssueAsync(client, "capability", permission: _scope);

        using HttpResponseMessage response = await PostOperationAsync(
            client,
            "capability",
            issued.Token,
            user: "another-user");

        await AssertDeniedAsync(factory, response, SignedCapabilityGrantReasonCodes.SubjectMismatch);
    }

    /// <summary>
    /// Verifies that a valid token does not bypass acknowledgment: an authorized administrator is still challenged.
    /// </summary>
    [Fact]
    public async Task ConsequentialAction_ValidToken_StillRequiresAcknowledgment()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: true);
        using HttpClient client = factory.CreateHttpsClient();

        CapabilityTokenResponse issued = await IssueAsync(
            client,
            "consequential",
            role: "administrator",
            permission: _scope);
        using HttpResponseMessage response = await PostOperationAsync(
            client,
            "consequential",
            issued.Token,
            role: "administrator");

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        Assert.DoesNotContain(
            "Consequential action executed.",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that a token is issued only to an actor who holds the capability the operation requires.
    /// </summary>
    [Fact]
    public async Task Issue_ActorWithoutCapabilityClaim_IsForbidden()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: true);
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostIssueAsync(
            client,
            "capability",
            role: null,
            permission: null,
            payload: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Verifies that only the sample capability-protected operations accept token requests.
    /// </summary>
    [Fact]
    public async Task Issue_UnknownOperation_IsRejected()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: true);
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostIssueAsync(
            client,
            "administrator",
            role: null,
            permission: _scope,
            payload: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Verifies that the issuance route is unavailable while tokens are disabled and the claim model applies.
    /// </summary>
    [Fact]
    public async Task Issue_TokensDisabled_ReturnsNotFound()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: false);
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostIssueAsync(
            client,
            "capability",
            role: null,
            permission: _scope,
            payload: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Verifies that the claim-backed model still authorizes, and still denies, as before while tokens are disabled.
    /// </summary>
    [Fact]
    public async Task CapabilityAction_TokensDisabled_ClaimModelBehavesAsBefore()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(tokensEnabled: false);
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage withClaim = await PostOperationAsync(client, "capability", token: null, permission: _scope);
        using HttpResponseMessage withoutClaim = await PostOperationAsync(client, "capability", token: null);

        Assert.Equal(HttpStatusCode.OK, withClaim.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, withoutClaim.StatusCode);
        Assert.IsType<ClaimEndpointCapabilityGrantValidator>(
            factory.Services.CreateScope().ServiceProvider
                .GetRequiredService<AsiBackbone.AspNetCore.Endpoints.IEndpointCapabilityGrantValidator>());
    }

    /// <summary>
    /// Verifies that startup fails clearly when tokens are enabled without a signing key.
    /// </summary>
    [Fact]
    public void Startup_TokensEnabledWithoutSigningKey_Fails()
    {
        using WebApplicationFactory<Program> factory = ApplicationWebApplicationFactory.CreateAllowingAnonymousAccess()
            .WithWebHostBuilder(builder => ApplyCapabilityTokenSettings(builder, enabled: true, withKeys: false));

        // ValidateOnStart runs while the host starts, so the factory fails to start rather than serve a request. The host
        // reports every failed options validation together; AsiBackbone's grant options read the capability token
        // options, so the same failure surfaces through both.
        AggregateException startupFailure = Assert.Throws<AggregateException>(() => factory.Services);

        Assert.NotEmpty(startupFailure.InnerExceptions);
        Assert.All(startupFailure.InnerExceptions, static exception =>
        {
            Assert.IsType<OptionsValidationException>(exception);
            Assert.Contains(
                "ReferenceApplication:CapabilityTokens:SigningKeyPem is required when capability tokens are enabled",
                exception.Message,
                StringComparison.Ordinal);
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _database.Dispose();
    }

    private static async Task AssertDeniedAsync(
        WebApplicationFactory<Program> factory,
        HttpResponseMessage response,
        string expectedReasonCode)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(expectedReasonCode, Assert.Single(await ReadSingleDecisionReasonCodesAsync(factory)));
    }

    private static async Task<AuditLedgerRecord> FindRecordAsync(ApplicationDbContext dbContext, string recordId)
    {
        return Assert.IsType<AuditLedgerRecord>(await new EfCoreAuditLedgerStore(dbContext).FindByRecordIdAsync(
            recordId,
            TestContext.Current.CancellationToken));
    }

    private static async Task<IReadOnlyList<string>> ReadSingleDecisionReasonCodesAsync(
        WebApplicationFactory<Program> factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        AuditLedgerRecordEntity decision = await dbContext.AsiBackboneAuditLedgerRecords
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Null(decision.CapabilityTokenId);
        AuditLedgerRecord record = await FindRecordAsync(dbContext, decision.RecordId);
        return record.ReasonCodes;
    }

    private static async Task<string> IssueDirectAsync(
        WebApplicationFactory<Program> factory,
        CapabilityTokenResponse reference,
        string? operationName = null,
        IReadOnlyList<string>? scopes = null)
    {
        // Signed with the application's own key, so only the field under test differs from a properly issued token.
        CapabilityGrant referenceGrant = CapabilityGrantToken.Decode(reference.Token).SignedGrant!.Artifact;
        CapabilityTokenIssuer issuer = factory.Services.GetRequiredService<CapabilityTokenIssuer>();

        IssuedCapabilityToken issued = await issuer.IssueAsync(
            new CapabilityTokenIssueRequest(
                referenceGrant.SubjectId!,
                operationName ?? reference.OperationName,
                scopes ?? reference.Scopes,
                []),
            TestContext.Current.CancellationToken);

        return issued.Token;
    }

    private static string EditToken(string token, Action<JsonObject> edit)
    {
        JsonObject document = JsonNode
            .Parse(Base64Url.DecodeFromChars(token.AsSpan(CapabilityGrantToken.FormatPrefix.Length)))!
            .AsObject();
        edit(document);
        return CapabilityGrantToken.FormatPrefix + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(document.ToJsonString()));
    }

    private static async Task<CapabilityTokenResponse> IssueAsync(
        HttpClient client,
        string operation,
        string? role = null,
        string? permission = null,
        IReadOnlyDictionary<string, string>? payload = null)
    {
        using HttpResponseMessage response = await PostIssueAsync(client, operation, role, permission, payload);
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Issuance returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");

        return Assert.IsType<CapabilityTokenResponse>(
            await response.Content.ReadFromJsonAsync<CapabilityTokenResponse>(TestContext.Current.CancellationToken));
    }

    private static async Task<HttpResponseMessage> PostIssueAsync(
        HttpClient client,
        string operation,
        string? role,
        string? permission,
        IReadOnlyDictionary<string, string>? payload)
    {
        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            [_antiforgeryFormFieldName] = await GetAntiforgeryTokenAsync(client, "test-user", role, permission),
            ["operation"] = operation
        };

        if (payload is not null)
        {
            foreach (KeyValuePair<string, string> field in payload)
            {
                form[CapabilityTokensController.PayloadFieldPrefix + field.Key] = field.Value;
            }
        }

        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, _tokensPath, "test-user", role, permission);
        request.Content = new FormUrlEncodedContent(form);

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> PostOperationAsync(
        HttpClient client,
        string action,
        string? token,
        string? role = null,
        string? permission = null,
        IReadOnlyDictionary<string, string>? form = null,
        string user = "test-user")
    {
        Dictionary<string, string> fields = new(StringComparer.Ordinal)
        {
            [_antiforgeryFormFieldName] = await GetAntiforgeryTokenAsync(client, user, role, permission)
        };

        if (form is not null)
        {
            foreach (KeyValuePair<string, string> field in form)
            {
                fields[field.Key] = field.Value;
            }
        }

        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Post,
            $"{_operationsPath}/{action}",
            user,
            role,
            permission);
        request.Headers.Accept.ParseAdd("application/problem+json");
        request.Content = new FormUrlEncodedContent(fields);

        if (token is not null)
        {
            request.Headers.Add(SignedCapabilityGrantOptions.DefaultTokenHeaderName, token);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string user, string? role, string? permission)
    {
        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Get,
            "/test/authentication/antiforgery-token",
            user,
            role,
            permission);
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        string user,
        string? role,
        string? permission)
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

    private Task<WebApplicationFactory<Program>> CreateFactoryAsync(bool tokensEnabled)
    {
        Dictionary<string, string?> configuration = new()
        {
            ["ReferenceApplication:Authentication:Enabled"] = "true",
            ["ReferenceApplication:Authentication:DefaultScheme"] = _testAuthenticationScheme,
            ["ReferenceApplication:Authentication:DefaultChallengeScheme"] = _testAuthenticationScheme,
            ["ReferenceApplication:Authentication:DefaultSignInScheme"] = _testAuthenticationScheme,
            ["ReferenceApplication:Authentication:Cookie:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Cookie:Scheme"] = "Cookies",
            ["ReferenceApplication:Authorization:RoleClaimType"] = ApplicationClaimTypes.Role,
            ["ReferenceApplication:Authorization:PermissionClaimType"] = ApplicationClaimTypes.Permission,
            ["ReferenceApplication:Authorization:AdministratorRoles:0"] = "administrator",
            ["ReferenceApplication:AuditCompletionOutbox:Enabled"] = "false"
        };

        // The capability grant model is selected at service registration, before test configuration sources are
        // applied, so its settings are supplied as host settings, which are visible that early.
        return _database.ApplyAsync(ApplicationWebApplicationFactory.CreateAllowingAnonymousAccess(configuration)
            .WithWebHostBuilder(builder => ApplyCapabilityTokenSettings(builder, tokensEnabled, withKeys: true)
                .ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
                services
                    .AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                        _testAuthenticationScheme,
                        _ => { });
            })));
    }

    private static IWebHostBuilder ApplyCapabilityTokenSettings(IWebHostBuilder builder, bool enabled, bool withKeys)
    {
        builder
            .UseSetting($"{CapabilityTokenOptions.SectionName}:Enabled", enabled ? "true" : "false")
            .UseSetting(
                $"{CapabilityTokenOptions.SectionName}:LifetimeSeconds",
                _lifetimeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // The private key signs at issuance; verification is configured with the public key only, as a production
        // relying endpoint would be.
        return enabled && withKeys
            ? builder
                .UseSetting($"{CapabilityTokenOptions.SectionName}:SigningKeyPem", _signingKey.Value.PrivateKeyPem)
                .UseSetting($"{CapabilityTokenOptions.SectionName}:VerificationKeys:0:KeyVersion", "1")
                .UseSetting($"{CapabilityTokenOptions.SectionName}:VerificationKeys:0:PublicKeyPem", _signingKey.Value.PublicKeyPem)
            : builder;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public void Advance(TimeSpan delta)
        {
            _utcNow = _utcNow.Add(delta);
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
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
            if (!Request.Headers.TryGetValue(_testUserHeaderName, out StringValues user)
                || string.IsNullOrWhiteSpace(user))
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
