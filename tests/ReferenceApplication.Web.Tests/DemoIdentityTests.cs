using System.Net;
using AsiBackbone.Core.Decisions;
using AsiBackbone.EntityFrameworkCore.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReferenceApplication.Infrastructure.Data;
using ReferenceApplication.Web.Authentication.Demo;
using ReferenceApplication.Web.Tests.Extensions;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies that the fixed-claim demo identity can exercise the real governed endpoint pipeline only when explicitly
/// enabled in Development.
/// </summary>
public sealed class DemoIdentityTests : IDisposable
{
    private const string _antiforgeryFormFieldName = "__RequestVerificationToken";
    private const string _demoEnabledKey = "ReferenceApplication:DemoIdentity:Enabled";
    private readonly TestSqliteDatabase _database = TestSqliteDatabase.Create();

    /// <summary>
    /// Verifies the fixed claim combinations produce allowed, denied, and acknowledgment-required decisions through
    /// cookie authentication, authorization, antiforgery, and endpoint governance.
    /// </summary>
    /// <param name="administrator">Whether the demo actor has the administrator role.</param>
    /// <param name="capability">Whether the demo actor has the consequential capability.</param>
    /// <param name="action">The governed operation to invoke.</param>
    /// <param name="expectedStatus">The expected HTTP result.</param>
    /// <param name="expectedOutcome">The expected persisted governance outcome.</param>
    [Theory]
    [InlineData(true, false, "administrator", HttpStatusCode.OK, GovernanceDecisionOutcome.Allowed)]
    [InlineData(false, false, "administrator", HttpStatusCode.Forbidden, GovernanceDecisionOutcome.Denied)]
    [InlineData(false, true, "capability", HttpStatusCode.OK, GovernanceDecisionOutcome.Allowed)]
    [InlineData(false, false, "capability", HttpStatusCode.Forbidden, GovernanceDecisionOutcome.Denied)]
    [InlineData(true, true, "consequential", HttpStatusCode.PreconditionRequired, GovernanceDecisionOutcome.AcknowledgmentRequired)]
    [InlineData(true, false, "consequential", HttpStatusCode.Forbidden, GovernanceDecisionOutcome.Denied)]
    public async Task DemoIdentity_DevelopmentEnabled_ExercisesGovernanceOutcomes(
        bool administrator,
        bool capability,
        string action,
        HttpStatusCode expectedStatus,
        GovernanceDecisionOutcome expectedOutcome)
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(Environments.Development, enabled: true);
        Assert.NotNull(factory.Services.GetService<IDemoIdentityService>());
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage login = await client.GetAsync(
            "/Account/Login",
            TestContext.Current.CancellationToken);
        string loginPage = await login.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(DemoIdentityOptions.SignInPath, loginPage, StringComparison.Ordinal);
        Assert.Contains(_antiforgeryFormFieldName, loginPage, StringComparison.Ordinal);

        using HttpResponseMessage signIn = await SignInAsync(client, administrator, capability);
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.Equal("/", signIn.Headers.Location?.OriginalString);

        using HttpResponseMessage response = await PostGovernedOperationAsync(client, action);
        Assert.Equal(expectedStatus, response.StatusCode);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AuditLedgerRecordEntity decision = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .AsiBackboneAuditLedgerRecords
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("referenceapplication-demo-actor", decision.ActorId);
        Assert.Equal(expectedOutcome.ToString(), decision.Outcome);
    }

    /// <summary>
    /// Verifies that the demo control is not registered when either activation condition is absent.
    /// </summary>
    /// <param name="environmentName">The host environment.</param>
    /// <param name="enabled">The explicit switch value.</param>
    [Theory]
    [InlineData("Development", false)]
    [InlineData("Staging", true)]
    [InlineData("Production", true)]
    public async Task DemoIdentity_NotActive_IsAbsentAndCannotAuthenticate(
        string environmentName,
        bool enabled)
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(environmentName, enabled);
        Assert.Null(factory.Services.GetService<IDemoIdentityService>());
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage login = await client.GetAsync(
            "/Account/Login",
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(
            DemoIdentityOptions.SignInPath,
            await login.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);

        using HttpResponseMessage signIn = await SignInAsync(client, administrator: true, capability: true);
        Assert.Equal(HttpStatusCode.NotFound, signIn.StatusCode);

        using HttpResponseMessage governed = await PostGovernedOperationAsync(client, "administrator");
        Assert.Equal(HttpStatusCode.Unauthorized, governed.StatusCode);
    }

    /// <summary>
    /// Verifies that activating the demo route does not bypass antiforgery validation.
    /// </summary>
    [Fact]
    public async Task DemoIdentity_WithoutAntiforgeryToken_ReturnsBadRequest()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(Environments.Development, enabled: true);
        using HttpClient client = factory.CreateHttpsClient();
        using HttpResponseMessage response = await client.PostAsync(
            DemoIdentityOptions.SignInPath,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["administrator"] = bool.TrueString,
                ["returnUrl"] = "/"
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _database.Dispose();
    }

    private async Task<WebApplicationFactory<Program>> CreateFactoryAsync(
        string environmentName,
        bool enabled)
    {
        WebApplicationFactory<Program> factory =
            ApplicationWebApplicationFactory.CreateAllowingAnonymousAccess(new Dictionary<string, string?>
            {
                [_demoEnabledKey] = enabled.ToString()
            }, environmentName)
            .WithWebHostBuilder(builder => builder.UseSetting(_demoEnabledKey, enabled.ToString()));

        return await _database.ApplyAsync(factory);
    }

    private static async Task<HttpResponseMessage> SignInAsync(
        HttpClient client,
        bool administrator,
        bool capability)
    {
        string token = await GetAntiforgeryTokenAsync(client);
        Dictionary<string, string> form = new()
        {
            [_antiforgeryFormFieldName] = token,
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

        return await client.PostAsync(
            DemoIdentityOptions.SignInPath,
            new FormUrlEncodedContent(form),
            TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> PostGovernedOperationAsync(HttpClient client, string action)
    {
        string token = await GetAntiforgeryTokenAsync(client);
        return await client.PostAsync(
            $"/api/v1/governed-operations/{action}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                [_antiforgeryFormFieldName] = token
            }),
            TestContext.Current.CancellationToken);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync(
            "/test/authentication/antiforgery-token",
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }
}
