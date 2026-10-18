using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AsiBackbone.AspNetCore.Endpoints;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ReferenceApplication.Web.Authentication.Claims;
using ReferenceApplication.Web.Governance;
using ReferenceApplication.Web.Tests.Extensions;
using ReferenceApplication.Web.Tests.Infrastructure;
using ReferenceApplication.Web.Tests.TestControllers;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies that the Step 4 governance rules are enforced through AsiBackbone's real endpoint metadata and middleware,
/// not only when evaluation metadata is constructed by hand.
/// </summary>
/// <remarks>
/// Each rule recognizes its policy marker by reading <c>endpoint.policy_types</c> from the evaluation metadata. If that
/// format ever differs from what the rules expect, every rule returns <c>NotApplicable</c> and governed endpoints are
/// silently allowed. These tests pin the contract to AsiBackbone's actual output and exercise each rule end to end.
/// </remarks>
public sealed class GovernanceEnforcementTests
{
    private const string _testAuthenticationScheme = "GovernanceTestAuthentication";
    private const string _testUserHeaderName = "X-Test-User";
    private const string _testRoleHeaderName = "X-Test-Role";

    /// <summary>
    /// Verifies that the metadata AsiBackbone produces for a real endpoint identifies each attached policy marker, and
    /// only those markers.
    /// </summary>
    [Fact]
    public void EndpointMetadata_FromRealDescriptor_IdentifiesAttachedPolicyMarkers()
    {
        IReadOnlyDictionary<string, string> metadata = CreateEndpointMetadata(
            EndpointGovernanceMetadataMode.Full,
            new GovernancePolicyAttribute(typeof(AdministratorActionPolicy)),
            new GovernancePolicyAttribute(typeof(ConsequentialActionPolicy)));

        Assert.True(GovernanceMetadata.ContainsPolicy<AdministratorActionPolicy>(metadata));
        Assert.True(GovernanceMetadata.ContainsPolicy<ConsequentialActionPolicy>(metadata));
        Assert.False(GovernanceMetadata.ContainsPolicy<CapabilityProtectedActionPolicy>(metadata));
    }

    /// <summary>
    /// Verifies that the metadata AsiBackbone produces for a real endpoint carries a declared capability scope in the
    /// form the capability-declaration rule reads.
    /// </summary>
    [Fact]
    public void EndpointMetadata_FromRealDescriptor_IdentifiesDeclaredCapabilityScope()
    {
        IReadOnlyDictionary<string, string> metadata = CreateEndpointMetadata(
            EndpointGovernanceMetadataMode.Full,
            new GovernancePolicyAttribute(typeof(CapabilityProtectedActionPolicy)),
            new RequireCapabilityGrantAttribute(ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope));

        Assert.True(GovernanceMetadata.ContainsPolicy<CapabilityProtectedActionPolicy>(metadata));
        Assert.True(GovernanceMetadata.ContainsCapabilityScope(
            metadata,
            ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope));
    }

    /// <summary>
    /// Documents why the host must keep full endpoint metadata. Reduced metadata still identifies policy markers, so
    /// no rule fails open, but it omits capability scopes, so the capability-declaration rule would reject every
    /// capability-protected endpoint, including correctly declared ones.
    /// </summary>
    [Fact]
    public void EndpointMetadata_ReducedMode_KeepsPolicyMarkersButOmitsCapabilityScopes()
    {
        IReadOnlyDictionary<string, string> metadata = CreateEndpointMetadata(
            EndpointGovernanceMetadataMode.Reduced,
            new GovernancePolicyAttribute(typeof(CapabilityProtectedActionPolicy)),
            new RequireCapabilityGrantAttribute(ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope));

        Assert.True(GovernanceMetadata.ContainsPolicy<CapabilityProtectedActionPolicy>(metadata));
        Assert.False(GovernanceMetadata.ContainsCapabilityScope(
            metadata,
            ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope));
    }

    /// <summary>
    /// Verifies that the host keeps full endpoint metadata, which the capability-declaration rule requires to see a
    /// declared scope.
    /// </summary>
    [Fact]
    public void Host_UsesFullEndpointMetadataMode()
    {
        using var factory = ApplicationWebApplicationFactory.CreateAllowingAnonymousAccess();

        EndpointGovernanceOptions options =
            factory.Services.GetRequiredService<IOptions<EndpointGovernanceOptions>>().Value;

        Assert.Equal(EndpointGovernanceMetadataMode.Full, options.MetadataMode);
    }

    /// <summary>
    /// Verifies that an administrator-governed endpoint is blocked before its action runs when the actor lacks an
    /// administrator role.
    /// </summary>
    [Fact]
    public async Task AdministratorEndpoint_NonAdministrator_IsBlockedBeforeActionExecutes()
    {
        using WebApplicationFactory<Program> factory = CreateFactoryWithTestAuthentication();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostAsync(client, "administrator-action", role: "reader");

        await AssertBlockedAsync(response);
    }

    /// <summary>
    /// Verifies that an anonymous caller cannot satisfy the administrator rule.
    /// </summary>
    [Fact]
    public async Task AdministratorEndpoint_AnonymousCaller_IsBlockedBeforeActionExecutes()
    {
        using WebApplicationFactory<Program> factory = CreateFactoryWithTestAuthentication();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostAsync(client, "administrator-action", user: null);

        await AssertBlockedAsync(response);
    }

    /// <summary>
    /// Verifies that an administrator-governed endpoint executes for an actor in a configured administrator role.
    /// </summary>
    [Fact]
    public async Task AdministratorEndpoint_Administrator_ExecutesAction()
    {
        using WebApplicationFactory<Program> factory = CreateFactoryWithTestAuthentication();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostAsync(client, "administrator-action", role: "administrator");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            AsiBackboneTestController.ExecutedMarker,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that a consequential endpoint does not execute until the required acknowledgment is provided.
    /// </summary>
    [Fact]
    public async Task ConsequentialEndpoint_WithoutAcknowledgment_IsBlockedBeforeActionExecutes()
    {
        using WebApplicationFactory<Program> factory = CreateFactoryWithTestAuthentication();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostAsync(client, "consequential-action", role: "administrator");

        await AssertBlockedAsync(response);
    }

    /// <summary>
    /// Verifies that an endpoint carrying the capability-protected marker without the required scope declaration is
    /// rejected, even for an administrator.
    /// </summary>
    [Fact]
    public async Task CapabilityEndpoint_WithoutDeclaredScope_IsBlockedBeforeActionExecutes()
    {
        using WebApplicationFactory<Program> factory = CreateFactoryWithTestAuthentication();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await PostAsync(
            client,
            "capability-action-without-scope",
            role: "administrator");

        await AssertBlockedAsync(response);
    }

    private static IReadOnlyDictionary<string, string> CreateEndpointMetadata(
        EndpointGovernanceMetadataMode mode,
        params object[] endpointMetadata)
    {
        Endpoint endpoint = new(
            static _ => Task.CompletedTask,
            new EndpointMetadataCollection(endpointMetadata),
            "Governance contract test endpoint");

        return EndpointGovernanceDescriptor.FromEndpoint(endpoint).ToMetadata(mode);
    }

    private static async Task AssertBlockedAsync(HttpResponseMessage response)
    {
        Assert.False(
            response.IsSuccessStatusCode,
            $"Expected the governed request to be blocked, but it returned {(int)response.StatusCode}.");
        Assert.DoesNotContain(
            AsiBackboneTestController.ExecutedMarker,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string action,
        string? user = "test-user",
        string? role = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"/test/asibackbone/{action}");

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

    private static WebApplicationFactory<Program> CreateFactoryWithTestAuthentication()
    {
        return ApplicationWebApplicationFactory.CreateAllowingAnonymousAccess(new Dictionary<string, string?>
        {
            ["ReferenceApplication:Authentication:Enabled"] = "true",
            ["ReferenceApplication:Authentication:DefaultScheme"] = _testAuthenticationScheme,
            ["ReferenceApplication:Authentication:DefaultChallengeScheme"] = _testAuthenticationScheme,
            ["ReferenceApplication:Authentication:DefaultSignInScheme"] = _testAuthenticationScheme,
            ["ReferenceApplication:Authentication:Cookie:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Cookie:Scheme"] = "Cookies",
            ["ReferenceApplication:Authorization:RoleClaimType"] = ApplicationClaimTypes.Role,
            ["ReferenceApplication:Authorization:AdministratorRoles:0"] = "administrator"
        })
        .WithWebHostBuilder(builder => builder.ConfigureServices(services => services
            .AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, RoleHeaderAuthenticationHandler>(
                _testAuthenticationScheme,
                _ => { })));
    }

    /// <summary>
    /// Provides a test-only authentication handler that issues a subject and an optional application role from request
    /// headers.
    /// </summary>
    private sealed class RoleHeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        /// <summary>
        /// Authenticates the request when the test user header is present.
        /// </summary>
        /// <returns>An authentication result for the current test request.</returns>
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(_testUserHeaderName, out StringValues user) ||
                string.IsNullOrWhiteSpace(user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            List<Claim> claims = [new Claim("sub", user.ToString())];

            if (Request.Headers.TryGetValue(_testRoleHeaderName, out StringValues role) &&
                !string.IsNullOrWhiteSpace(role))
            {
                claims.Add(new Claim(ApplicationClaimTypes.Role, role.ToString()));
            }

            ClaimsPrincipal principal = new(new ClaimsIdentity(claims, Scheme.Name));

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
