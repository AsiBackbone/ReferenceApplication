using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using AsiBackbone.AspNetCore.Acknowledgments;
using AsiBackbone.AspNetCore.Actors;
using AsiBackbone.AspNetCore.Correlation;
using AsiBackbone.AspNetCore.Endpoints;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ReferenceApplication.Web.Authentication.Claims;
using ReferenceApplication.Web.Tests.Extensions;
using ReferenceApplication.Web.Tests.Infrastructure;
using ReferenceApplication.Web.Tests.TestControllers;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Provides integration tests for the AsiBackbone composition added to the complete web host in walkthrough Step 3:
/// service registration, host-owned actor mapping, and endpoint-governance middleware placement.
/// </summary>
public sealed class AsiBackboneIntegrationTests
{
    private const string _testAuthenticationScheme = "AsiBackboneTestAuthentication";
    private const string _testSubjectHeaderName = "X-Test-Subject";
    private const string _testNameIdentifierHeaderName = "X-Test-Name-Identifier";
    private const string _testDisplayNameHeaderName = "X-Test-Display-Name";

    /// <summary>
    /// Verifies that the complete web host starts with AsiBackbone's integration services registered and resolvable
    /// from a request scope.
    /// </summary>
    [Fact]
    public void Host_RegistersAsiBackboneIntegrationServices()
    {
        using var factory = ApplicationWebApplicationFactory.CreateAllowingAnonymousAccess();
        using IServiceScope scope = factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<IHttpGovernanceActorContextResolver>());
        Assert.NotNull(scope.ServiceProvider.GetService<IHttpGovernanceRequestCorrelationResolver>());
        Assert.NotNull(scope.ServiceProvider.GetService<IAcknowledgmentChallengeService>());
        Assert.NotNull(scope.ServiceProvider.GetService<IEndpointGovernanceService>());
    }

    /// <summary>
    /// Verifies that the host applies the application-owned actor claim mapping, not AsiBackbone's defaults alone.
    /// </summary>
    [Fact]
    public void Host_ConfiguresApplicationActorClaimMapping()
    {
        using var factory = ApplicationWebApplicationFactory.CreateAllowingAnonymousAccess();

        HttpGovernanceActorContextOptions options =
            factory.Services.GetRequiredService<IOptions<HttpGovernanceActorContextOptions>>().Value;

        Assert.Equal(ApplicationClaimTypes.Subject, options.ActorIdClaimTypes.First());
        Assert.Equal(ApplicationClaimTypes.Name, options.DisplayNameClaimTypes.First());
    }

    /// <summary>
    /// Verifies that endpoint governance remains opt-in at Step 3, so an endpoint without governance metadata passes
    /// through the middleware unchanged.
    /// </summary>
    [Fact]
    public async Task UngovernedEndpoint_PassesThroughEndpointGovernanceMiddleware()
    {
        using var factory = ApplicationWebApplicationFactory.CreateAllowingAnonymousAccess();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await client.GetAsync(
            "/test/asibackbone/ungoverned",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            AsiBackboneTestController.ExecutedMarker,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the endpoint-governance middleware runs after routing has selected the endpoint and before the
    /// endpoint executes: a governed endpoint is stopped with the configured configuration-failure status because
    /// Step 3 deliberately registers no policy evaluator.
    /// </summary>
    [Fact]
    public async Task GovernedEndpoint_IsStoppedByMiddlewareBeforeActionExecutes()
    {
        using var factory = ApplicationWebApplicationFactory.CreateAllowingAnonymousAccess();
        using HttpClient client = factory.CreateHttpsClient();
        int configurationFailureStatusCode = factory.Services
            .GetRequiredService<IOptions<EndpointGovernanceOptions>>().Value.ConfigurationFailureStatusCode;

        using HttpResponseMessage response = await client.GetAsync(
            "/test/asibackbone/governed",
            TestContext.Current.CancellationToken);

        Assert.Equal(configurationFailureStatusCode, (int)response.StatusCode);
        Assert.DoesNotContain(
            AsiBackboneTestController.ExecutedMarker,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies the complete actor path through the host: provider claims are normalized by the NCAT claims
    /// transformation, and AsiBackbone resolves the actor from the normalized application subject rather than the
    /// provider's name identifier.
    /// </summary>
    [Fact]
    public async Task AuthenticatedRequest_ResolvesActorFromNormalizedApplicationClaims()
    {
        using WebApplicationFactory<Program> factory = CreateFactoryWithTestAuthentication();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/test/asibackbone/actor");
        request.Headers.Add(_testSubjectHeaderName, "subject-123");
        request.Headers.Add(_testNameIdentifierHeaderName, "provider-id-456");
        request.Headers.Add(_testDisplayNameHeaderName, "Ada Lovelace");

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = await ReadJsonAsync(response);
        JsonElement root = document.RootElement;

        string? applicationSubject = root.GetProperty("applicationSubject").GetString();
        Assert.False(string.IsNullOrWhiteSpace(applicationSubject));
        Assert.Equal(applicationSubject, root.GetProperty("actorId").GetString());
        Assert.NotEqual("provider-id-456", root.GetProperty("actorId").GetString());
        Assert.Equal("Ada Lovelace", root.GetProperty("displayName").GetString());
        Assert.Equal("Human", root.GetProperty("actorType").GetString());
        Assert.True(root.GetProperty("isAuthenticated").GetBoolean());
        Assert.True(root.GetProperty("isKnown").GetBoolean());
    }

    /// <summary>
    /// Verifies that an anonymous request resolves to an unauthenticated, unknown actor.
    /// </summary>
    [Fact]
    public async Task AnonymousRequest_ResolvesUnauthenticatedActor()
    {
        using WebApplicationFactory<Program> factory = CreateFactoryWithTestAuthentication();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await client.GetAsync(
            "/test/asibackbone/actor",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = await ReadJsonAsync(response);
        JsonElement root = document.RootElement;

        Assert.False(root.GetProperty("isAuthenticated").GetBoolean());
        Assert.False(root.GetProperty("isKnown").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("applicationSubject").ValueKind);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return JsonDocument.Parse(body);
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
            ["ReferenceApplication:Authentication:Cookie:Scheme"] = "Cookies"
        })
        .WithWebHostBuilder(builder => builder.ConfigureServices(services => services
            .AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ProviderClaimsAuthenticationHandler>(
                _testAuthenticationScheme,
                _ => { })));
    }

    /// <summary>
    /// Provides a test-only authentication handler that issues provider-style claims from request headers, so the
    /// application claims transformation and the AsiBackbone actor resolver both run as they would for a real
    /// provider.
    /// </summary>
    private sealed class ProviderClaimsAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        /// <summary>
        /// Authenticates the request when the test subject header is present.
        /// </summary>
        /// <returns>An authentication result for the current test request.</returns>
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(_testSubjectHeaderName, out StringValues subject) ||
                string.IsNullOrWhiteSpace(subject))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            List<Claim> claims = [new Claim("sub", subject.ToString())];

            if (Request.Headers.TryGetValue(_testNameIdentifierHeaderName, out StringValues nameIdentifier))
            {
                claims.Add(new Claim(ClaimTypes.NameIdentifier, nameIdentifier.ToString()));
            }

            if (Request.Headers.TryGetValue(_testDisplayNameHeaderName, out StringValues displayName))
            {
                claims.Add(new Claim("name", displayName.ToString()));
            }

            ClaimsPrincipal principal = new(new ClaimsIdentity(claims, Scheme.Name));

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
