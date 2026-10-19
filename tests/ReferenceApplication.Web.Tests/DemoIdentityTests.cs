using System.Net;
using AsiBackbone.Core.Decisions;
using AsiBackbone.EntityFrameworkCore.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ReferenceApplication.Infrastructure.Data;
using ReferenceApplication.Infrastructure.Data.Options;
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
        Assert.Null(factory.Services.GetService<IDemonstrationDatabaseService>());
        Assert.Equal(
            "ApplicationDatabase",
            factory.Services.GetRequiredService<IOptions<DataAccessOptions>>().Value.ConnectionStringName);
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

        string resetToken = await GetAntiforgeryTokenAsync(client);
        using HttpResponseMessage reset = await client.PostAsync(
            DemoIdentityOptions.ResetDatabasePath,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                [_antiforgeryFormFieldName] = resetToken
            }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, reset.StatusCode);

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

    /// <summary>
    /// Verifies the administrator-only Development action removes demonstration records and evidence, then reapplies
    /// the real migration history so the explorer is immediately ready for another run.
    /// </summary>
    [Fact]
    public async Task DemonstrationDatabase_DevelopmentAdministrator_ResetReturnsEmptyMigratedState()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(Environments.Development, enabled: true);
        await using (AsyncServiceScope serviceScope = factory.Services.CreateAsyncScope())
        {
            Assert.NotNull(serviceScope.ServiceProvider.GetService<IDemonstrationDatabaseService>());
            Assert.Equal(
                DemoIdentityOptions.DatabaseConnectionStringName,
                serviceScope.ServiceProvider.GetRequiredService<IOptions<DataAccessOptions>>().Value.ConnectionStringName);
        }
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage signIn = await SignInAsync(client, administrator: true, capability: false);
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);

        using HttpResponseMessage create = await PostGovernedOperationAsync(client, "records");
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        await using (AsyncServiceScope populatedScope = factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext populated = populatedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(1, await populated.GovernedOperationRecords.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await populated.AsiBackboneAuditLedgerRecords.CountAsync(TestContext.Current.CancellationToken));
            Assert.NotEmpty(await populated.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
        }

        string token = await GetAntiforgeryTokenAsync(client);
        using HttpResponseMessage reset = await client.PostAsync(
            DemoIdentityOptions.ResetDatabasePath,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                [_antiforgeryFormFieldName] = token
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        Assert.Equal("/governance-explorer?databaseReset=true", reset.Headers.Location?.OriginalString);

        using HttpResponseMessage resetPage = await client.GetAsync(
            reset.Headers.Location,
            TestContext.Current.CancellationToken);
        string resetPageHtml = await resetPage.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resetPage.StatusCode);
        Assert.Contains("data-database-reset=\"true\"", resetPageHtml, StringComparison.Ordinal);
        Assert.Contains("migrated evidence state is empty", resetPageHtml, StringComparison.OrdinalIgnoreCase);

        await using AsyncServiceScope emptyScope = factory.Services.CreateAsyncScope();
        ApplicationDbContext empty = emptyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.NotEmpty(await empty.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await empty.GovernedOperationRecords.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await empty.AsiBackboneAuditLedgerRecords.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await empty.AuditRecords.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await empty.ApplicationAuditCompletionOutboxEntries.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await empty.AsiBackboneDecisionReceiptLifecycleEvents.CountAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies an authenticated Development demo actor without the administrator role cannot reset the database.
    /// </summary>
    [Fact]
    public async Task DemonstrationDatabase_DevelopmentNonAdministrator_ResetIsForbidden()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(Environments.Development, enabled: true);
        using HttpClient client = factory.CreateHttpsClient();
        using HttpResponseMessage signIn = await SignInAsync(client, administrator: false, capability: true);
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);

        string token = await GetAntiforgeryTokenAsync(client);
        using HttpResponseMessage reset = await client.PostAsync(
            DemoIdentityOptions.ResetDatabasePath,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                [_antiforgeryFormFieldName] = token
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        Assert.Equal("/Account/AccessDenied", reset.Headers.Location?.AbsolutePath);
    }

    /// <summary>
    /// Verifies the Development reset action cannot be invoked without an antiforgery token.
    /// </summary>
    [Fact]
    public async Task DemonstrationDatabase_WithoutAntiforgeryToken_ReturnsBadRequest()
    {
        using WebApplicationFactory<Program> factory = await CreateFactoryAsync(Environments.Development, enabled: true);
        using HttpClient client = factory.CreateHttpsClient();
        using HttpResponseMessage signIn = await SignInAsync(client, administrator: true, capability: false);
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);

        using HttpResponseMessage reset = await client.PostAsync(
            DemoIdentityOptions.ResetDatabasePath,
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);
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
                [_demoEnabledKey] = enabled.ToString(),
                ["ReferenceApplication:AuditCompletionOutbox:Enabled"] = "false"
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
        Dictionary<string, string> form = new()
        {
            [_antiforgeryFormFieldName] = token
        };
        if (string.Equals(action, "records", StringComparison.Ordinal))
        {
            form["Title"] = "Resettable demonstration record";
        }

        return await client.PostAsync(
            $"/api/v1/governed-operations/{action}",
            new FormUrlEncodedContent(form),
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
