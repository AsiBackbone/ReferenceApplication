using System.Net;
using AsiBackbone.Core.Emissions;
using AsiBackbone.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ReferenceApplication.Infrastructure.Data.Auditing;
using ReferenceApplication.Web.Extensions;
using ReferenceApplication.Web.Options;
using ReferenceApplication.Web.Tests.Extensions;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Provides integration and configuration tests for the application OpenTelemetry foundation.
/// </summary>
public sealed class OpenTelemetryTests
{
    private const string _releaseVersion = "0.4.0";

    /// <summary>
    /// Verifies that application OpenTelemetry options are bound from configuration.
    /// </summary>
    [Fact]
    public void OpenTelemetryOptions_Bind_DefaultConfiguration()
    {
        using ApplicationWebApplicationFactory factory = CreateFactory(new Dictionary<string, string?>
        {
            ["ReferenceApplication:OpenTelemetry:Enabled"] = "true",
            ["ReferenceApplication:OpenTelemetry:ServiceName"] = "ReferenceApplication.Web",
            ["ReferenceApplication:OpenTelemetry:ServiceVersion"] = _releaseVersion,
            ["ReferenceApplication:OpenTelemetry:EnableTracing"] = "true",
            ["ReferenceApplication:OpenTelemetry:EnableMetrics"] = "true",
            ["ReferenceApplication:OpenTelemetry:EnableAspNetCoreInstrumentation"] = "true",
            ["ReferenceApplication:OpenTelemetry:EnableHttpClientInstrumentation"] = "true",
            ["ReferenceApplication:OpenTelemetry:EnableGovernanceInstrumentation"] = "true",
            ["ReferenceApplication:OpenTelemetry:Otlp:Enabled"] = "false",
            ["ReferenceApplication:OpenTelemetry:Otlp:Endpoint"] = "",
            ["ReferenceApplication:OpenTelemetry:Otlp:Protocol"] = "Grpc"
        });

        ApplicationOpenTelemetryOptions options = factory.Services
            .GetRequiredService<IOptions<ApplicationOpenTelemetryOptions>>()
            .Value;

        Assert.True(options.Enabled);
        Assert.Equal("ReferenceApplication.Web", options.ServiceName);
        Assert.Equal(_releaseVersion, options.ServiceVersion);
        Assert.True(options.EnableTracing);
        Assert.True(options.EnableMetrics);
        Assert.True(options.EnableAspNetCoreInstrumentation);
        Assert.True(options.EnableHttpClientInstrumentation);
        Assert.True(options.EnableGovernanceInstrumentation);
        Assert.True(new ApplicationOpenTelemetryOptions().EnableGovernanceInstrumentation);

        Assert.False(options.Otlp.Enabled);
        Assert.Equal(string.Empty, options.Otlp.Endpoint);
        Assert.Equal("Grpc", options.Otlp.Protocol);
    }

    /// <summary>
    /// Verifies that OpenTelemetry options validation fails when OTLP export is enabled with an invalid endpoint.
    /// </summary>
    [Fact]
    public void OpenTelemetryOptions_InvalidOtlpEndpoint_FailsValidationWhenExporterEnabled()
    {
        OptionsValidationException exception =
            AssertOpenTelemetryOptionsValidationFails(
                new Dictionary<string, string?>
                {
                    ["ReferenceApplication:OpenTelemetry:Enabled"] = "true",
                    ["ReferenceApplication:OpenTelemetry:ServiceName"] = "ReferenceApplication.Web",
                    ["ReferenceApplication:OpenTelemetry:ServiceVersion"] = _releaseVersion,
                    ["ReferenceApplication:OpenTelemetry:EnableTracing"] = "true",
                    ["ReferenceApplication:OpenTelemetry:EnableMetrics"] = "true",
                    ["ReferenceApplication:OpenTelemetry:EnableAspNetCoreInstrumentation"] = "true",
                    ["ReferenceApplication:OpenTelemetry:EnableHttpClientInstrumentation"] = "true",
                    ["ReferenceApplication:OpenTelemetry:Otlp:Enabled"] = "true",
                    ["ReferenceApplication:OpenTelemetry:Otlp:Endpoint"] = "not-a-valid-uri",
                    ["ReferenceApplication:OpenTelemetry:Otlp:Protocol"] = "Grpc"
                });

        Assert.Contains(
            "ReferenceApplication:OpenTelemetry:Otlp:Endpoint must be a valid absolute URI when OTLP export is enabled",
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the application starts when OpenTelemetry is enabled and OTLP export is disabled.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Application_Starts_WhenOpenTelemetryEnabledWithoutOtlpExporter()
    {
        using ApplicationWebApplicationFactory factory = CreateFactory(new Dictionary<string, string?>
        {
            ["ReferenceApplication:OpenTelemetry:Enabled"] = "true",
            ["ReferenceApplication:OpenTelemetry:ServiceName"] = "ReferenceApplication.Web",
            ["ReferenceApplication:OpenTelemetry:ServiceVersion"] = _releaseVersion,
            ["ReferenceApplication:OpenTelemetry:EnableTracing"] = "true",
            ["ReferenceApplication:OpenTelemetry:EnableMetrics"] = "true",
            ["ReferenceApplication:OpenTelemetry:EnableAspNetCoreInstrumentation"] = "true",
            ["ReferenceApplication:OpenTelemetry:EnableHttpClientInstrumentation"] = "true",
            ["ReferenceApplication:OpenTelemetry:Otlp:Enabled"] = "false",
            ["ReferenceApplication:OpenTelemetry:Otlp:Endpoint"] = "",
            ["ReferenceApplication:OpenTelemetry:Otlp:Protocol"] = "Grpc"
        });

        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Verifies that the application starts when OpenTelemetry is disabled.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task Application_Starts_WhenOpenTelemetryDisabled()
    {
        using ApplicationWebApplicationFactory factory = CreateFactory(new Dictionary<string, string?>
        {
            ["ReferenceApplication:OpenTelemetry:Enabled"] = "false",
            ["ReferenceApplication:OpenTelemetry:ServiceName"] = "ReferenceApplication.Web",
            ["ReferenceApplication:OpenTelemetry:ServiceVersion"] = _releaseVersion,
            ["ReferenceApplication:OpenTelemetry:EnableTracing"] = "true",
            ["ReferenceApplication:OpenTelemetry:EnableMetrics"] = "true",
            ["ReferenceApplication:OpenTelemetry:EnableAspNetCoreInstrumentation"] = "true",
            ["ReferenceApplication:OpenTelemetry:EnableHttpClientInstrumentation"] = "true",
            ["ReferenceApplication:OpenTelemetry:Otlp:Enabled"] = "false",
            ["ReferenceApplication:OpenTelemetry:Otlp:Endpoint"] = "",
            ["ReferenceApplication:OpenTelemetry:Otlp:Protocol"] = "Grpc"
        });

        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    }
    /// <summary>
    /// Verifies which activity sources and meters the host collects under each setting, the single list the
    /// OpenTelemetry composition and the pipeline inspector both read.
    /// </summary>
    [Fact]
    public void TelemetrySources_GovernanceInstrumentation_FollowsTracingMetricsAndItsOwnSwitch()
    {
        ApplicationOpenTelemetryOptions defaults = new();
        Assert.True(ApplicationTelemetrySources.IsGovernanceInstrumentationActive(defaults));
        Assert.True(ApplicationTelemetrySources.IsActivitySourceCollected(defaults, OpenTelemetryGovernanceInstrumentation.ActivitySourceName));
        Assert.True(ApplicationTelemetrySources.IsMeterCollected(defaults, OpenTelemetryGovernanceInstrumentation.MeterName));
        Assert.True(ApplicationTelemetrySources.IsMeterCollected(defaults, ApplicationAuditReconciliationMetrics.MeterName));
        Assert.True(ApplicationTelemetrySources.IsActivitySourceCollected(defaults, ApplicationTelemetrySources.AspNetCoreActivitySourceName));
        Assert.False(ApplicationTelemetrySources.IsActivitySourceCollected(defaults, "Unrelated.Source"));
        Assert.False(ApplicationTelemetrySources.IsMeterCollected(defaults, "Unrelated.Meter"));

        ApplicationOpenTelemetryOptions governanceOff = new() { EnableGovernanceInstrumentation = false };
        Assert.False(ApplicationTelemetrySources.IsGovernanceInstrumentationActive(governanceOff));
        Assert.False(ApplicationTelemetrySources.IsActivitySourceCollected(governanceOff, OpenTelemetryGovernanceInstrumentation.ActivitySourceName));
        Assert.False(ApplicationTelemetrySources.IsMeterCollected(governanceOff, ApplicationAuditReconciliationMetrics.MeterName));
        Assert.True(ApplicationTelemetrySources.IsActivitySourceCollected(governanceOff, ApplicationTelemetrySources.AspNetCoreActivitySourceName));

        ApplicationOpenTelemetryOptions tracingOnly = new() { EnableMetrics = false };
        Assert.True(ApplicationTelemetrySources.IsGovernanceInstrumentationActive(tracingOnly));
        Assert.True(ApplicationTelemetrySources.IsActivitySourceCollected(tracingOnly, OpenTelemetryGovernanceInstrumentation.ActivitySourceName));
        Assert.False(ApplicationTelemetrySources.IsMeterCollected(tracingOnly, OpenTelemetryGovernanceInstrumentation.MeterName));

        Assert.False(ApplicationTelemetrySources.IsGovernanceInstrumentationActive(new() { EnableTracing = false, EnableMetrics = false }));
        Assert.False(ApplicationTelemetrySources.IsGovernanceInstrumentationActive(new() { Enabled = false }));
    }

    /// <summary>
    /// Verifies that AsiBackbone's governance emitter is registered only while governance instrumentation is active.
    /// </summary>
    [Fact]
    public void AddApplicationOpenTelemetry_RegistersGovernanceEmitterOnlyWhenActive()
    {
        _ = Assert.IsType<OpenTelemetryGovernanceEmitter>(BuildEmitter(new Dictionary<string, string?>()));
        Assert.Null(BuildEmitter(new Dictionary<string, string?>
        {
            ["ReferenceApplication:OpenTelemetry:EnableGovernanceInstrumentation"] = "false"
        }));
        Assert.Null(BuildEmitter(new Dictionary<string, string?>
        {
            ["ReferenceApplication:OpenTelemetry:Enabled"] = "false"
        }));
        Assert.Null(BuildEmitter(new Dictionary<string, string?>
        {
            ["ReferenceApplication:OpenTelemetry:EnableTracing"] = "false",
            ["ReferenceApplication:OpenTelemetry:EnableMetrics"] = "false"
        }));
    }

    /// <summary>
    /// Creates a test application factory with the supplied in-memory configuration overrides.
    /// </summary>
    /// <param name="configurationValues">The configuration key/value pairs used to override application settings for a test.</param>
    /// <returns>A configured <see cref="ApplicationWebApplicationFactory"/> instance.</returns>
    private static ApplicationWebApplicationFactory CreateFactory(IReadOnlyDictionary<string, string?> configurationValues)
    {
        return ApplicationWebApplicationFactory.CreateAllowingAnonymousAccess(configurationValues);
    }

    private static IGovernanceEmitter? BuildEmitter(IReadOnlyDictionary<string, string?> configurationValues)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();

        IServiceCollection services = new ServiceCollection();
        _ = services.AddLogging();
        _ = services.AddApplicationOpenTelemetry(configuration, new TestHostEnvironment());

        using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        return provider.GetService<IGovernanceEmitter>();
    }

    private static OptionsValidationException AssertOpenTelemetryOptionsValidationFails(
        IReadOnlyDictionary<string, string?> configurationValues)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();

        IServiceCollection services = new ServiceCollection();

        _ = services.AddApplicationOpenTelemetry(
            configuration,
            new TestHostEnvironment());

        using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        return Assert.Throws<OptionsValidationException>(() =>
            provider
                .GetRequiredService<IOptions<ApplicationOpenTelemetryOptions>>()
                .Value);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = "ReferenceApplication.Web.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
