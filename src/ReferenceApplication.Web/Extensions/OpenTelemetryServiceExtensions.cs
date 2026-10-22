using System.Reflection;
using AsiBackbone.DependencyInjection;
using AsiBackbone.OpenTelemetry;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ReferenceApplication.Web.Options;

namespace ReferenceApplication.Web.Extensions;

/// <summary>
/// Provides extension methods to register OpenTelemetry services for the application.
/// </summary>
public static class OpenTelemetryServiceExtensions
{
    /// <summary>
    /// Adds OpenTelemetry tracing and metrics: ASP.NET Core and HttpClient instrumentation, and governance
    /// instrumentation.
    /// </summary>
    /// <remarks>
    /// Governance instrumentation, on by default with <c>EnableGovernanceInstrumentation</c>, registers AsiBackbone's
    /// OpenTelemetry governance emitter through <c>UseOpenTelemetryEmission</c>, collects its activity source and meter,
    /// and collects the host's <c>ReferenceApplication.AuditReconciliation</c> gauges. The emitter records spans only
    /// while tracing is enabled and metrics only while metrics are enabled. <see cref="ApplicationTelemetrySources" />
    /// names every source and meter collected, and the Development pipeline inspector reads it too.
    /// </remarks>
    /// <param name="services">The service collection to add OpenTelemetry services to.</param>
    /// <param name="configuration">The application configuration source.</param>
    /// <param name="environment">The current hosting environment.</param>
    /// <returns>The same service collection instance so calls can be chained.</returns>
    public static IServiceCollection AddApplicationOpenTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services
            .AddOptions<ApplicationOpenTelemetryOptions>()
            .Bind(configuration.GetSection(ApplicationOpenTelemetryOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.ServiceName),
                "ReferenceApplication:OpenTelemetry:ServiceName must not be empty.")
            .Validate(options => !options.Otlp.Enabled || Uri.TryCreate(options.Otlp.Endpoint, UriKind.Absolute, out _),
                "ReferenceApplication:OpenTelemetry:Otlp:Endpoint must be a valid absolute URI when OTLP export is enabled.")
            .ValidateOnStart();

        ApplicationOpenTelemetryOptions options = configuration
            .GetSection(ApplicationOpenTelemetryOptions.SectionName)
            .Get<ApplicationOpenTelemetryOptions>() ?? new ApplicationOpenTelemetryOptions();

        if (!options.Enabled)
        {
            return services;
        }

        string serviceVersion = ResolveServiceVersion(options);
        bool governance = ApplicationTelemetrySources.IsGovernanceInstrumentationActive(options);

        if (governance)
        {
            services.AddAsiBackbone(builder => builder.UseOpenTelemetryEmission(emitter =>
            {
                emitter.EmitActivityEvents = options.EnableTracing;
                emitter.EmitMetrics = options.EnableMetrics;
            }));
        }

        OpenTelemetryBuilder openTelemetryBuilder = services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: options.ServiceName,
                    serviceVersion: serviceVersion)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment.name"] = environment.EnvironmentName
                }));

        if (options.EnableTracing)
        {
            openTelemetryBuilder.WithTracing(tracing =>
            {
                if (options.EnableAspNetCoreInstrumentation)
                {
                    tracing.AddAspNetCoreInstrumentation();
                }

                if (options.EnableHttpClientInstrumentation)
                {
                    tracing.AddHttpClientInstrumentation();
                }

                if (governance)
                {
                    tracing.AddSource([.. ApplicationTelemetrySources.GovernanceActivitySourceNames]);
                }

                if (options.Otlp.Enabled)
                {
                    tracing.AddOtlpExporter(exporterOptions => ConfigureOtlpExporter(exporterOptions, options.Otlp));
                }
            });
        }

        if (options.EnableMetrics)
        {
            openTelemetryBuilder.WithMetrics(metrics =>
            {
                if (options.EnableAspNetCoreInstrumentation)
                {
                    metrics.AddAspNetCoreInstrumentation();
                }

                if (options.EnableHttpClientInstrumentation)
                {
                    metrics.AddHttpClientInstrumentation();
                }

                if (governance)
                {
                    metrics.AddMeter([.. ApplicationTelemetrySources.GovernanceMeterNames]);
                }

                if (options.Otlp.Enabled)
                {
                    metrics.AddOtlpExporter(exporterOptions => ConfigureOtlpExporter(exporterOptions, options.Otlp));
                }
            });
        }

        return services;
    }

    private static void ConfigureOtlpExporter(
        OtlpExporterOptions exporterOptions,
        ApplicationOtlpExporterOptions options)
    {
        exporterOptions.Endpoint = new Uri(options.Endpoint);
        exporterOptions.Protocol = string.Equals(
            options.Protocol,
            "HttpProtobuf",
            StringComparison.OrdinalIgnoreCase)
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;
    }

    private static string ResolveServiceVersion(ApplicationOpenTelemetryOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ServiceVersion))
        {
            return options.ServiceVersion.Trim();
        }

        Assembly assembly = typeof(Program).Assembly;

        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        return !string.IsNullOrWhiteSpace(informationalVersion) ? informationalVersion : assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
