using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ReferenceApplication.Web.Authentication.Claims;
using ReferenceApplication.Web.Extensions;
using ReferenceApplication.Web.Governance.Demonstration;
using ReferenceApplication.Web.Tests.Extensions;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies the bounds, concurrency, live metrics, authorization, and environment boundary of the load demonstration.
/// </summary>
public sealed class GovernanceLoadDemonstrationTests
{
    private const string _authenticationScheme = "GovernanceLoadDemonstrationTestAuthentication";
    private const string _roleHeader = "X-Test-Role";
    private const string _userHeader = "X-Test-User";
    private const string _section = "ReferenceApplication:GovernanceLoadDemonstration";

    /// <summary>
    /// Verifies a second run cannot start until the active run is cancelled.
    /// </summary>
    [Fact]
    public async Task TryStart_WhileRunIsActive_RejectsSecondRunAndSupportsCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using ServiceProvider provider = CreateServiceProvider(new DelegateExecutor(async cancellationToken =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }));
        using GovernanceLoadDemonstrationService service = CreateService(provider);
        await using IGovernanceLoadDemonstrationSubscription subscription = service.Subscribe();

        GovernanceLoadDemonstrationStartResult first = service.TryStart(10, 1_000);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        GovernanceLoadDemonstrationStartResult second = service.TryStart(10, 1_000);

        Assert.Equal(GovernanceLoadDemonstrationStartStatus.Started, first.Status);
        Assert.Equal(GovernanceLoadDemonstrationStartStatus.AlreadyRunning, second.Status);
        Assert.True(service.TryCancel());
        GovernanceLoadDemonstrationSnapshot terminal = await ReadTerminalAsync(subscription);
        Assert.Equal(GovernanceLoadDemonstrationState.Cancelled, terminal.State);
    }

    /// <summary>
    /// Verifies requested iteration and duration bounds cannot exceed the configured hard limits.
    /// </summary>
    [Fact]
    public void TryStart_BoundsExceedMaximum_ReturnsInvalidBounds()
    {
        using ServiceProvider provider = CreateServiceProvider(new DelegateExecutor(_ => ValueTask.CompletedTask));
        using GovernanceLoadDemonstrationService service = CreateService(provider, maximumIterations: 5, maximumDuration: 50);

        GovernanceLoadDemonstrationStartResult tooManyIterations = service.TryStart(6, 50);
        GovernanceLoadDemonstrationStartResult tooLong = service.TryStart(5, 51);

        Assert.Equal(GovernanceLoadDemonstrationStartStatus.InvalidBounds, tooManyIterations.Status);
        Assert.Equal(GovernanceLoadDemonstrationStartStatus.InvalidBounds, tooLong.Status);
        Assert.Equal(GovernanceLoadDemonstrationState.Idle, service.Current.State);
    }

    /// <summary>
    /// Verifies configuration cannot raise the process safety ceilings.
    /// </summary>
    [Theory]
    [InlineData("MaximumIterations", GovernanceLoadDemonstrationOptions.AbsoluteMaximumIterations + 1)]
    [InlineData("MaximumDurationMilliseconds", GovernanceLoadDemonstrationOptions.AbsoluteMaximumDurationMilliseconds + 1)]
    public void Registration_ConfiguredMaximumExceedsAbsoluteCeiling_IsRejected(string setting, int value)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{_section}:Enabled"] = "true",
                [$"{_section}:{setting}"] = value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            })
            .Build();
        ServiceCollection services = new();
        services.AddApplicationGovernanceLoadDemonstration(
            configuration,
            new TestHostEnvironment(Environments.Development));
        using ServiceProvider provider = services.BuildServiceProvider();

        _ = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<GovernanceLoadDemonstrationOptions>>().Value);
    }

    /// <summary>
    /// Verifies the duration limit actively cancels an evaluation that has not completed.
    /// </summary>
    [Fact]
    public async Task Run_DurationLimitExpires_StopsAtHardCap()
    {
        using ServiceProvider provider = CreateServiceProvider(new DelegateExecutor(async cancellationToken =>
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)));
        using GovernanceLoadDemonstrationService service = CreateService(provider, maximumDuration: 50);
        await using IGovernanceLoadDemonstrationSubscription subscription = service.Subscribe();

        GovernanceLoadDemonstrationStartResult result = service.TryStart(10, 25);
        GovernanceLoadDemonstrationSnapshot terminal = await ReadTerminalAsync(subscription);

        Assert.Equal(GovernanceLoadDemonstrationStartStatus.Started, result.Status);
        Assert.Equal(GovernanceLoadDemonstrationState.DurationLimitReached, terminal.State);
        Assert.Equal(0, terminal.CompletedIterations);
    }

    /// <summary>
    /// Verifies a completed run publishes aggregate throughput and a latency histogram without exceeding its count.
    /// </summary>
    [Fact]
    public async Task Run_IterationBoundReached_PublishesAggregateMetrics()
    {
        using ServiceProvider provider = CreateServiceProvider(new DelegateExecutor(_ => ValueTask.CompletedTask));
        using GovernanceLoadDemonstrationService service = CreateService(provider, maximumIterations: 4);
        await using IGovernanceLoadDemonstrationSubscription subscription = service.Subscribe();

        GovernanceLoadDemonstrationStartResult result = service.TryStart(4, 1_000);
        GovernanceLoadDemonstrationSnapshot terminal = await ReadTerminalAsync(subscription);

        Assert.Equal(GovernanceLoadDemonstrationStartStatus.Started, result.Status);
        Assert.Equal(GovernanceLoadDemonstrationState.Completed, terminal.State);
        Assert.Equal(4, terminal.CompletedIterations);
        Assert.Equal(4, terminal.LatencyHistogram.Sum(static bucket => bucket.Count));
        Assert.True(terminal.ThroughputPerSecond > 0);
    }

    /// <summary>
    /// Verifies even an immediately completed run publishes its running state before its terminal state.
    /// </summary>
    [Fact]
    public async Task Run_ImmediateCompletion_PublishesStatesInOrder()
    {
        using ServiceProvider provider = CreateServiceProvider(new DelegateExecutor(_ => ValueTask.CompletedTask));
        using GovernanceLoadDemonstrationService service = CreateService(provider, maximumIterations: 1);
        await using IGovernanceLoadDemonstrationSubscription subscription = service.Subscribe();
        List<GovernanceLoadDemonstrationState> states = [];

        _ = service.TryStart(1, 1_000);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await foreach (GovernanceLoadDemonstrationSnapshot snapshot in subscription.ReadAllAsync(timeout.Token))
        {
            states.Add(snapshot.State);
            if (snapshot.State == GovernanceLoadDemonstrationState.Completed)
            {
                break;
            }
        }

        Assert.Equal(
            [
                GovernanceLoadDemonstrationState.Idle,
                GovernanceLoadDemonstrationState.Running,
                GovernanceLoadDemonstrationState.Completed
            ],
            states);
    }

    /// <summary>
    /// Verifies the mapped Development endpoint enforces the administrator policy.
    /// </summary>
    [Fact]
    public async Task StartEndpoint_DevelopmentNonAdministrator_IsForbidden()
    {
        using var database = TestSqliteDatabase.Create();
        using WebApplicationFactory<Program> factory = await CreateDevelopmentFactoryAsync(database);
        using HttpClient client = factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Add(_userHeader, "load-reader");
        client.DefaultRequestHeaders.Add(_roleHeader, "reader");
        string token = await GetAntiforgeryTokenAsync(client);

        using HttpResponseMessage response = await client.PostAsync(
            "/api/v1/governance-load-demonstration/start",
            CreateStartContent(token),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Verifies a Development administrator can start the mapped bounded run with antiforgery protection.
    /// </summary>
    [Fact]
    public async Task StartEndpoint_DevelopmentAdministrator_AcceptsBoundedRun()
    {
        using var database = TestSqliteDatabase.Create();
        using WebApplicationFactory<Program> factory = await CreateDevelopmentFactoryAsync(database);
        using HttpClient client = factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Add(_userHeader, "load-administrator");
        client.DefaultRequestHeaders.Add(_roleHeader, "administrator");
        string token = await GetAntiforgeryTokenAsync(client);

        using HttpResponseMessage response = await client.PostAsync(
            "/api/v1/governance-load-demonstration/start",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["iterations"] = "1",
                ["durationMilliseconds"] = "1000"
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    /// <summary>
    /// Verifies an administrator cannot start a run without a valid antiforgery token.
    /// </summary>
    [Fact]
    public async Task StartEndpoint_WithoutAntiforgeryToken_ReturnsBadRequest()
    {
        using var database = TestSqliteDatabase.Create();
        using WebApplicationFactory<Program> factory = await CreateDevelopmentFactoryAsync(database);
        using HttpClient client = factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Add(_userHeader, "load-administrator");
        client.DefaultRequestHeaders.Add(_roleHeader, "administrator");

        using HttpResponseMessage response = await client.PostAsync(
            "/api/v1/governance-load-demonstration/start",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Verifies the service and routes are absent outside Development even when the switch is true.
    /// </summary>
    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Feature_OutsideDevelopment_IsNotRegisteredOrMapped(string environmentName)
    {
        using WebApplicationFactory<Program> factory = ApplicationWebApplicationFactory
            .CreateAllowingAnonymousAccess(new Dictionary<string, string?>
            {
                [$"{_section}:Enabled"] = "true",
                ["ReferenceApplication:AuditCompletionOutbox:Enabled"] = "false"
            }, environmentName)
            .WithWebHostBuilder(builder => builder.UseSetting($"{_section}:Enabled", "true"));

        Assert.Null(factory.Services.GetService<IGovernanceLoadDemonstrationService>());
        using HttpClient client = factory.CreateHttpsClient();
        using HttpResponseMessage response = await client.PostAsync(
            "/api/v1/governance-load-demonstration/start",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static GovernanceLoadDemonstrationService CreateService(
        ServiceProvider provider,
        int maximumIterations = 10,
        int maximumDuration = 2_000)
    {
        return new GovernanceLoadDemonstrationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new GovernanceLoadDemonstrationOptions
            {
                MaximumIterations = maximumIterations,
                DefaultIterations = Math.Min(maximumIterations, 4),
                MaximumDurationMilliseconds = maximumDuration,
                DefaultDurationMilliseconds = Math.Min(maximumDuration, 1_000),
                ProgressIntervalMilliseconds = 10
            }),
            new TestApplicationLifetime(),
            NullLogger<GovernanceLoadDemonstrationService>.Instance);
    }

    private static ServiceProvider CreateServiceProvider(IGovernanceLoadDemonstrationExecutor executor)
    {
        ServiceCollection services = new();
        services.AddSingleton(executor);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    private static async Task<GovernanceLoadDemonstrationSnapshot> ReadTerminalAsync(
        IGovernanceLoadDemonstrationSubscription subscription)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await foreach (GovernanceLoadDemonstrationSnapshot snapshot in subscription.ReadAllAsync(timeout.Token))
        {
            if (snapshot.State is not (GovernanceLoadDemonstrationState.Idle or GovernanceLoadDemonstrationState.Running))
            {
                return snapshot;
            }
        }

        throw new InvalidOperationException("The demonstration did not publish a terminal state.");
    }

    private static async Task<WebApplicationFactory<Program>> CreateDevelopmentFactoryAsync(
        TestSqliteDatabase database)
    {
        // The feature is registered and mapped from configuration read during service registration, before test
        // configuration sources are applied, so the switch is also supplied as a host setting. Without it these tests
        // would depend on whatever the local appsettings.Development.json, user secrets, or environment variables say.
        WebApplicationFactory<Program> unconfigured = ApplicationWebApplicationFactory
            .CreateAllowingAnonymousAccess(new Dictionary<string, string?>
            {
                ["ReferenceApplication:Authentication:Enabled"] = "true",
                ["ReferenceApplication:Authentication:DefaultScheme"] = _authenticationScheme,
                ["ReferenceApplication:Authentication:DefaultChallengeScheme"] = _authenticationScheme,
                ["ReferenceApplication:Authentication:DefaultSignInScheme"] = _authenticationScheme,
                ["ReferenceApplication:Authorization:RoleClaimType"] = ApplicationClaimTypes.Role,
                ["ReferenceApplication:Authorization:AdministratorRoles:0"] = "administrator",
                ["ReferenceApplication:AuditCompletionOutbox:Enabled"] = "false",
                [$"{_section}:Enabled"] = "true"
            }, Environments.Development)
            .WithWebHostBuilder(builder => builder
                .UseSetting($"{_section}:Enabled", "true")
                .ConfigureServices(services =>
                    services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                        _authenticationScheme,
                        _ => { })));
        return await database.ApplyAsync(unconfigured);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync(
            "/test/authentication/antiforgery-token",
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static FormUrlEncodedContent CreateStartContent(string token)
    {
        return new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["iterations"] = "10",
            ["durationMilliseconds"] = "1000"
        });
    }

    private sealed class DelegateExecutor(
        Func<CancellationToken, ValueTask> evaluate) : IGovernanceLoadDemonstrationExecutor
    {
        public ValueTask EvaluateAsync(string correlationId, CancellationToken cancellationToken)
        {
            return evaluate(cancellationToken);
        }
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "ReferenceApplication.Web.Tests";

        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(_userHeader, out StringValues user) || string.IsNullOrWhiteSpace(user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            List<Claim> claims =
            [
                new(ApplicationClaimTypes.Subject, user.ToString()),
                new(ApplicationClaimTypes.Name, "Load demonstration test actor")
            ];
            if (Request.Headers.TryGetValue(_roleHeader, out StringValues role) && !string.IsNullOrWhiteSpace(role))
            {
                claims.Add(new Claim(ApplicationClaimTypes.Role, role.ToString()));
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

}
