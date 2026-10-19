using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ReferenceApplication.Web.Authentication.Claims;
using ReferenceApplication.Web.Tests.Extensions;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies the authenticated, CSP-compatible governance explorer page and its static assets.
/// </summary>
public sealed partial class GovernanceExplorerTests
{
    private const string _testAuthenticationScheme = "GovernanceExplorerTestAuthentication";
    private const string _testUserHeaderName = "X-Test-User";
    private const string _testRoleHeaderName = "X-Test-Role";

    /// <summary>
    /// Verifies that an administrator receives the complete two-pane explorer with antiforgery protection,
    /// accessible live regions, and external-only script and style resources.
    /// </summary>
    [Fact]
    public async Task Index_Administrator_RendersAccessibleCspCompatibleExplorer()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateHttpsClient();
        using HttpRequestMessage request = CreateRequest(role: "administrator");

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Governance Explorer", html, StringComparison.Ordinal);
        Assert.Contains("data-can-view-evidence=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("data-governed-action=\"administrator\"", html, StringComparison.Ordinal);
        Assert.Contains("data-governed-action=\"capability\"", html, StringComparison.Ordinal);
        Assert.Contains("data-governed-action=\"consequential\"", html, StringComparison.Ordinal);
        Assert.Contains("data-governed-action=\"records\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"__RequestVerificationToken\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\"", html, StringComparison.Ordinal);
        Assert.Contains("/css/governance-explorer.css", html, StringComparison.Ordinal);
        Assert.Contains("/js/governance-explorer.js", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" style=", html, StringComparison.OrdinalIgnoreCase);

        MatchCollection scripts = ScriptElementRegex().Matches(html);
        Assert.NotEmpty(scripts);
        Assert.All(scripts.Cast<Match>(), script =>
        {
            Assert.Contains(" src=", script.Groups["attributes"].Value, StringComparison.OrdinalIgnoreCase);
            Assert.True(string.IsNullOrWhiteSpace(script.Groups["body"].Value));
        });
    }

    /// <summary>
    /// Verifies that the page requires authentication.
    /// </summary>
    [Fact]
    public async Task Index_Anonymous_IsRejected()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await client.GetAsync(
            "/governance-explorer",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Verifies that an authenticated non-administrator can run operations but is clearly denied the full ledger.
    /// </summary>
    [Fact]
    public async Task Index_NonAdministrator_RendersActionsWithEvidenceBoundary()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateHttpsClient();
        using HttpRequestMessage request = CreateRequest(role: "reader");

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data-can-view-evidence=\"false\"", html, StringComparison.Ordinal);
        Assert.Contains("full ledger requires an administrator", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data-governed-action=\"administrator\"", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the explorer JavaScript and stylesheet are served as static resources under the existing CSP.
    /// </summary>
    [Fact]
    public async Task StaticAssets_AreServedWithoutAuthentication()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage script = await client.GetAsync(
            "/js/governance-explorer.js",
            TestContext.Current.CancellationToken);
        using HttpResponseMessage stylesheet = await client.GetAsync(
            "/css/governance-explorer.css",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, stylesheet.StatusCode);
        Assert.Equal("text/css", stylesheet.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// Verifies that the existing fixed-claim demo controls appear on the explorer only when explicitly activated in
    /// Development and reflect the signed-in actor's selected role and capability.
    /// </summary>
    [Fact]
    public async Task Index_DevelopmentDemoIdentity_ShowsSelectedFixedClaims()
    {
        const string demoEnabledKey = "ReferenceApplication:DemoIdentity:Enabled";
        using WebApplicationFactory<Program> factory =
            ApplicationWebApplicationFactory.CreateAllowingAnonymousAccess(new Dictionary<string, string?>
            {
                [demoEnabledKey] = "true",
                ["ReferenceApplication:AuditCompletionOutbox:Enabled"] = "false"
            }, Environments.Development)
            .WithWebHostBuilder(builder => builder.UseSetting(demoEnabledKey, "true"));
        using HttpClient client = factory.CreateHttpsClient();
        using HttpResponseMessage tokenResponse = await client.GetAsync(
            "/test/authentication/antiforgery-token",
            TestContext.Current.CancellationToken);
        string token = await tokenResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using HttpResponseMessage signIn = await client.PostAsync(
            "/Account/DemoSignIn",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["administrator"] = bool.TrueString,
                ["consequentialCapability"] = bool.TrueString,
                ["returnUrl"] = "/governance-explorer"
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.Equal("/governance-explorer", signIn.Headers.Location?.OriginalString);

        using HttpResponseMessage response = await client.GetAsync(
            "/governance-explorer",
            TestContext.Current.CancellationToken);
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("action=\"/Account/DemoSignIn\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"explorer-administrator\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"explorer-capability\"", html, StringComparison.Ordinal);
        Assert.Contains("Governance demo actor", html, StringComparison.Ordinal);
        Assert.Contains("data-can-view-evidence=\"true\"", html, StringComparison.Ordinal);
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        return ApplicationWebApplicationFactory.CreateAllowingAnonymousAccess(new Dictionary<string, string?>
        {
            ["ReferenceApplication:Authentication:Enabled"] = "true",
            ["ReferenceApplication:Authentication:DefaultScheme"] = _testAuthenticationScheme,
            ["ReferenceApplication:Authentication:DefaultChallengeScheme"] = _testAuthenticationScheme,
            ["ReferenceApplication:Authentication:DefaultSignInScheme"] = _testAuthenticationScheme,
            ["ReferenceApplication:AuditCompletionOutbox:Enabled"] = "false",
            ["ReferenceApplication:Authorization:RoleClaimType"] = ApplicationClaimTypes.Role,
            ["ReferenceApplication:Authorization:AdministratorRoles:0"] = "administrator"
        }).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                _testAuthenticationScheme,
                _ => { })));
    }

    private static HttpRequestMessage CreateRequest(string role)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/governance-explorer");
        request.Headers.Add(_testUserHeaderName, "explorer-user");
        request.Headers.Add(_testRoleHeaderName, role);
        return request;
    }

    [GeneratedRegex("<script\\b(?<attributes>[^>]*)>(?<body>.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptElementRegex();

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

            List<Claim> claims =
            [
                new(ApplicationClaimTypes.Subject, user.ToString()),
                new(ApplicationClaimTypes.Name, "Explorer test actor")
            ];
            if (Request.Headers.TryGetValue(_testRoleHeaderName, out StringValues role) && !string.IsNullOrWhiteSpace(role))
            {
                claims.Add(new Claim(ApplicationClaimTypes.Role, role.ToString()));
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
