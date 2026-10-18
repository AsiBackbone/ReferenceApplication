using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ReferenceApplication.Web.Authentication.Claims;
using ReferenceApplication.Web.Governance;
using ReferenceApplication.Web.Tests.Extensions;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies the Step 5 production endpoints through authorization, antiforgery, and AsiBackbone endpoint governance.
/// </summary>
public sealed class GovernedOperationsEndpointTests : IDisposable
{
    private readonly TestSqliteDatabase _database = TestSqliteDatabase.Create();

    private const string _testAuthenticationScheme = "GovernedOperationsTestAuthentication";
    private const string _testUserHeaderName = "X-Test-User";
    private const string _testRoleHeaderName = "X-Test-Role";
    private const string _testPermissionHeaderName = "X-Test-Permission";
    private const string _antiforgeryFormFieldName = "__RequestVerificationToken";
    private const string _basePath = "/api/v1/governed-operations";

    /// <summary>
    /// Verifies that the read-only description remains explicitly anonymous and ungoverned.
    /// </summary>
    [Fact]
    public async Task Description_AnonymousCaller_ReturnsWithoutGovernance()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryWithTestAuthenticationAsync();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await client.GetAsync(
            _basePath,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "describe-governed-operations",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies the administrator policy's allowed path on an application endpoint.
    /// </summary>
    [Fact]
    public async Task AdministratorAction_Administrator_Executes()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryWithTestAuthenticationAsync();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostAsync(client, "administrator", role: "administrator");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "Administrator-governed action executed.",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that administrator denial maps to the application's Problem Details convention.
    /// </summary>
    [Fact]
    public async Task AdministratorAction_NonAdministrator_ReturnsProblemDetails()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryWithTestAuthenticationAsync();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostAsync(client, "administrator", role: "reader");

        await AssertGovernanceProblemAsync(response);
    }

    /// <summary>
    /// Verifies that a matching normalized permission claim satisfies the endpoint's declared capability scope.
    /// </summary>
    [Fact]
    public async Task CapabilityAction_ActorWithGrant_Executes()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryWithTestAuthenticationAsync();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostAsync(
            client,
            "capability",
            permission: ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "Capability-protected action executed.",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that declaring a capability on the endpoint does not substitute for an actor-held grant.
    /// </summary>
    [Fact]
    public async Task CapabilityAction_ActorWithoutGrant_ReturnsProblemDetails()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryWithTestAuthenticationAsync();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostAsync(client, "capability");

        await AssertGovernanceProblemAsync(response);
    }

    /// <summary>
    /// Verifies that an otherwise-authorized consequential request is challenged before its action executes.
    /// </summary>
    [Fact]
    public async Task ConsequentialAction_AuthorizedActor_RequiresAcknowledgment()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryWithTestAuthenticationAsync();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostAsync(
            client,
            "consequential",
            role: "administrator",
            permission: ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            ConsequentialActionDecisionPolicy.AcknowledgmentReasonCode,
            document.RootElement.GetProperty("reasonCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("handshakeId").GetString()));
        Assert.DoesNotContain(
            "Consequential action executed.",
            document.RootElement.GetRawText(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that an administrator without the capability grant is denied on the consequential endpoint rather
    /// than offered an acknowledgment challenge: acknowledgment must never stand in for a missing grant.
    /// </summary>
    /// <remarks>
    /// Requires AsiBackbone 7.2.1 or later, which validates capability grants for acknowledgment-required decisions
    /// (AsiBackbone/AsiBackbone#887). On 7.2.0 this request received an HTTP 428 challenge
    /// (AsiBackbone/ReferenceApplication#17).
    /// </remarks>
    [Fact]
    public async Task ConsequentialAction_AdministratorWithoutGrant_IsDeniedNotChallenged()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryWithTestAuthenticationAsync();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostAsync(client, "consequential", role: "administrator");

        await AssertGovernanceProblemAsync(response);
        Assert.DoesNotContain(
            "Consequential action executed.",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "handshakeId",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that holding the capability grant does not substitute for the administrator role on the consequential
    /// endpoint.
    /// </summary>
    [Fact]
    public async Task ConsequentialAction_GrantWithoutAdministratorRole_IsDeniedNotChallenged()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryWithTestAuthenticationAsync();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostAsync(
            client,
            "consequential",
            role: "reader",
            permission: ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope);

        await AssertGovernanceProblemAsync(response);
    }

    /// <summary>
    /// Verifies that the controller's authenticated-user policy rejects anonymous callers before endpoint governance
    /// runs, so governance never evaluates an anonymous actor for these operations.
    /// </summary>
    [Fact]
    public async Task AdministratorAction_AnonymousCaller_IsChallengedByAuthorization()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryWithTestAuthenticationAsync();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpRequestMessage request = new(HttpMethod.Post, $"{_basePath}/administrator")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>())
        };
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(
            "Administrator-governed action executed.",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that endpoint governance does not bypass the application's antiforgery requirement.
    /// </summary>
    [Fact]
    public async Task AdministratorAction_WithoutAntiforgeryToken_IsRejected()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryWithTestAuthenticationAsync();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostAsync(
            client,
            "administrator",
            role: "administrator",
            includeAntiforgeryToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(
            "Administrator-governed action executed.",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    private static async Task AssertGovernanceProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        JsonElement root = document.RootElement;
        Assert.Equal(403, root.GetProperty("status").GetInt32());
        Assert.Equal("Endpoint governance denied the request.", root.GetProperty("title").GetString());
        Assert.Equal(
            "The request did not satisfy the endpoint governance requirements.",
            root.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("requestId").GetString()));
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string action,
        string user = "test-user",
        string? role = null,
        string? permission = null,
        bool includeAntiforgeryToken = true)
    {
        Dictionary<string, string> form = new(StringComparer.Ordinal);

        if (includeAntiforgeryToken)
        {
            using HttpRequestMessage tokenRequest = CreateRequest(
                HttpMethod.Get,
                "/test/authentication/antiforgery-token",
                user,
                role,
                permission);
            using HttpResponseMessage tokenResponse = await client.SendAsync(
                tokenRequest,
                TestContext.Current.CancellationToken);
            tokenResponse.EnsureSuccessStatusCode();

            form[_antiforgeryFormFieldName] = await tokenResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);
        }

        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Post,
            $"{_basePath}/{action}",
            user,
            role,
            permission);
        request.Headers.Accept.ParseAdd("application/problem+json");
        request.Content = new FormUrlEncodedContent(form);

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
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

    /// <inheritdoc />
    public void Dispose()
    {
        _database.Dispose();
    }

    private Task<WebApplicationFactory<Program>> CreateFactoryWithTestAuthenticationAsync()
    {
        return _database.ApplyAsync(ApplicationWebApplicationFactory.CreateAllowingAnonymousAccess(new Dictionary<string, string?>
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
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                _testAuthenticationScheme,
                _ => { }))));
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
