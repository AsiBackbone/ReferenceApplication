using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ReferenceApplication.Web.Authentication.Demo;
using ReferenceApplication.Web.Extensions;
using ReferenceApplication.Web.Governance.Demonstration;
using ReferenceApplication.Web.Governance.FailureScenarios;
using ReferenceApplication.Web.Governance.Pipeline;
using ReferenceApplication.Web.Governance.Replay;
using ReferenceApplication.Web.Governance.Simulation;
using ReferenceApplication.Web.Tests.Extensions;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies that the production-shaped reference configuration in <c>docker-compose.production.yml</c> composes: the
/// host starts in <c>Production</c> with explicit host and proxy trust, an external identity provider, and a
/// certificate-encrypted key ring, becomes ready, and registers and maps none of the Development-only components.
/// </summary>
/// <remarks>
/// The CI container smoke test runs the same configuration in the built image. This test fails fast in the ordinary
/// test run, and also checks what the container probe cannot see from outside: the service registrations and the full
/// endpoint table.
/// </remarks>
public sealed class ProductionDeploymentTests : IDisposable
{
    private const string _host = "localhost";
    private const string _certificatePassword = "production-test-only";

    private static readonly string[] _developmentOnlyRoutePrefixes =
    [
        DemoIdentityOptions.SignInPath,
        DemoIdentityOptions.ResetDatabasePath,
        "/api/v1/governance-load-demonstration",
        PolicySimulatorServiceExtensions.EvaluatePath,
        "/api/v1/decision-replay",
        PipelineInspectorServiceExtensions.SnapshotPath,
        FailureScenarioServiceExtensions.RoutePrefix
    ];

    private readonly TestSqliteDatabase _database = TestSqliteDatabase.Create();
    private readonly string _workingDirectory = Directory.CreateTempSubdirectory("referenceapplication-production-").FullName;

    /// <summary>
    /// Verifies that the production-shaped host becomes ready and offers the configured identity provider instead of
    /// the demo identity.
    /// </summary>
    [Fact]
    public async Task ProductionConfiguration_BecomesReady_WithTheExternalProviderAndNoDemoIdentity()
    {
        using WebApplicationFactory<Program> factory = await CreateProductionFactoryAsync();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        string login = await client.GetStringAsync("/Account/Login", TestContext.Current.CancellationToken);
        Assert.Contains("OpenID Connect", login, StringComparison.Ordinal);
        Assert.DoesNotContain(DemoIdentityOptions.SignInPath, login, StringComparison.Ordinal);
        Assert.DoesNotContain("Development demo identity", login, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that host filtering rejects a Host header outside the configured public host names.
    /// </summary>
    [Fact]
    public async Task ProductionConfiguration_RejectsAnUnlistedHost()
    {
        using WebApplicationFactory<Program> factory = await CreateProductionFactoryAsync();
        using HttpClient client = factory.CreateHttpsClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Host = "attacker.example";

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Verifies that no Development-only service is registered and no Development-only route is mapped.
    /// </summary>
    [Fact]
    public async Task ProductionConfiguration_RegistersAndMapsNoDevelopmentOnlyComponent()
    {
        using WebApplicationFactory<Program> factory = await CreateProductionFactoryAsync();

        Assert.Null(factory.Services.GetService<IDemoIdentityService>());
        Assert.Null(factory.Services.GetService<IGovernanceLoadDemonstrationService>());
        Assert.Null(factory.Services.GetService<FailureScenarioRegistry>());
        Assert.Null(factory.Services.GetService<FailureScenarioSaveChangesInterceptor>());
        Assert.Null(factory.Services.GetService<PipelineTelemetryRecorder>());
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            Assert.Null(scope.ServiceProvider.GetService<IDemonstrationDatabaseService>());
            Assert.Null(scope.ServiceProvider.GetService<IPolicySimulator>());
            Assert.Null(scope.ServiceProvider.GetService<IDecisionReplayService>());
            Assert.Null(scope.ServiceProvider.GetService<IPipelineInspector>());
            Assert.Null(scope.ServiceProvider.GetService<IFailureScenarioService>());
        }

        string[] routes = [.. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(static endpoint => "/" + endpoint.RoutePattern.RawText?.TrimStart('/'))];
        Assert.NotEmpty(routes);
        foreach (string prefix in _developmentOnlyRoutePrefixes)
        {
            Assert.DoesNotContain(routes, route => route.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Verifies that an anonymous caller reaches no Development-only route: the fallback authorization policy sends
    /// every one of them, mapped or not, to the sign-in page.
    /// </summary>
    /// <remarks>
    /// Because the fallback policy answers mapped and unmapped routes alike, an outside probe cannot prove a route is
    /// absent. The container smoke test therefore reads the startup posture line, which reports the host's own
    /// registrations, and this test checks that line's source directly.
    /// </remarks>
    [Fact]
    public async Task ProductionConfiguration_SendsAnonymousDevelopmentOnlyRequestsToSignIn()
    {
        using WebApplicationFactory<Program> factory = await CreateProductionFactoryAsync();
        using HttpClient client = factory.CreateHttpsClient();

        foreach (string path in _developmentOnlyRoutePrefixes)
        {
            using HttpResponseMessage post = await client.PostAsync(path, new FormUrlEncodedContent([]), TestContext.Current.CancellationToken);
            Assert.True(
                post.StatusCode == HttpStatusCode.Redirect && post.Headers.Location?.AbsolutePath == "/Account/Login",
                $"POST {path} returned {(int)post.StatusCode} {post.Headers.Location}.");
        }

        Assert.Empty(StartupSecurityPostureExtensions.GetRegisteredDevelopmentOnlyComponents(factory.Services));
    }

    /// <summary>
    /// Verifies that the key ring is written to the configured durable location and its keys are encrypted at rest with
    /// the injected certificate.
    /// </summary>
    [Fact]
    public async Task ProductionConfiguration_EncryptsTheDurableKeyRing()
    {
        using WebApplicationFactory<Program> factory = await CreateProductionFactoryAsync();
        using HttpClient client = factory.CreateHttpsClient();

        // The sign-in page issues an antiforgery token, which needs a Data Protection key.
        using HttpResponseMessage login = await client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        string keyFile = Assert.Single(Directory.GetFiles(KeyRingPath, "key-*.xml"));
        string key = await File.ReadAllTextAsync(keyFile, TestContext.Current.CancellationToken);
        Assert.Contains("encryptedSecret", key, StringComparison.Ordinal);
        Assert.DoesNotContain("<masterKey", key.Replace("encryptedSecret", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _database.Dispose();
        try
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A lingering handle only leaves a temporary directory behind; it must not fail the test.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string KeyRingPath => Path.Combine(_workingDirectory, "data-protection-keys");

    private Task<WebApplicationFactory<Program>> CreateProductionFactoryAsync()
    {
        string certificatePath = CreateKeyEncryptionCertificate();

        // The same settings docker-compose.production.yml supplies, with test values for the required secrets.
        Dictionary<string, string?> production = new()
        {
            ["AllowedHosts"] = _host,
            ["ReferenceApplication:ForwardedHeaders:Enabled"] = "true",
            ["ReferenceApplication:ForwardedHeaders:ClearKnownNetworksAndProxies"] = "false",
            ["ReferenceApplication:ForwardedHeaders:KnownNetworks:0"] = "172.31.250.0/24",
            ["ReferenceApplication:DataProtection:ApplicationName"] = "ReferenceApplication.Web",
            ["ReferenceApplication:DataProtection:KeyRingPath"] = KeyRingPath,
            ["ReferenceApplication:DataProtection:KeyEncryptionCertificatePath"] = certificatePath,
            ["ReferenceApplication:DataProtection:KeyEncryptionCertificatePassword"] = _certificatePassword,
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:Authority"] = "https://identity.example.invalid",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:ClientId"] = "referenceapplication",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:ClientSecret"] = "production-test-only",
            ["ReferenceApplication:HealthChecks:DatabaseReadinessCheckEnabled"] = "true",
            ["ReferenceApplication:AuditCompletionOutbox:PollInterval"] = "01:00:00"
        };

        // Production uses the application's own default authorization, so the anonymous-access test override is not
        // applied. Settings read during service registration are pinned as host settings.
        return _database.ApplyAsync(new ApplicationWebApplicationFactory(production, "Production")
            .WithWebHostBuilder(builder =>
            {
                foreach ((string key, string? value) in production)
                {
                    _ = builder.UseSetting(key, value);
                }

                _ = builder
                    .UseSetting("ReferenceApplication:DemoIdentity:Enabled", "false")
                    .UseSetting("ReferenceApplication:GovernanceLoadDemonstration:Enabled", "false")
                    .UseSetting("ReferenceApplication:PolicySimulator:Enabled", "false")
                    .UseSetting("ReferenceApplication:DecisionReplay:Enabled", "false")
                    .UseSetting("ReferenceApplication:PipelineInspector:Enabled", "false")
                    .UseSetting("ReferenceApplication:FailureScenarios:Enabled", "false")
                    .UseSetting("ReferenceApplication:CapabilityTokens:Enabled", "false");
            }));
    }

    private string CreateKeyEncryptionCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=ReferenceApplication Data Protection (test)",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));

        string path = Path.Combine(_workingDirectory, "dataprotection-certificate.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, _certificatePassword));
        return path;
    }
}
