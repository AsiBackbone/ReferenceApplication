using AsiBackbone.OpenTelemetry;
using ReferenceApplication.Infrastructure.Data.Auditing;
using ReferenceApplication.Web.Options;

namespace ReferenceApplication.Web.Extensions;

/// <summary>
/// Names the activity sources and meters the host's OpenTelemetry configuration collects, and the settings that select
/// each, so the OpenTelemetry composition and the Development pipeline inspector cannot disagree about what is exported.
/// </summary>
internal static class ApplicationTelemetrySources
{
    /// <summary>
    /// The activity source of ASP.NET Core's server spans.
    /// </summary>
    public const string AspNetCoreActivitySourceName = "Microsoft.AspNetCore";

    /// <summary>
    /// The activity source of HttpClient's client spans.
    /// </summary>
    public const string HttpClientActivitySourceName = "System.Net.Http";

    /// <summary>
    /// Gets the meters OpenTelemetry's ASP.NET Core instrumentation adds.
    /// </summary>
    public static IReadOnlyList<string> AspNetCoreMeterNames { get; } =
    [
        "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Server.Kestrel",
        "Microsoft.AspNetCore.Http.Connections",
        "Microsoft.AspNetCore.Routing",
        "Microsoft.AspNetCore.Diagnostics",
        "Microsoft.AspNetCore.RateLimiting"
    ];

    /// <summary>
    /// Gets the meters OpenTelemetry's HttpClient instrumentation adds.
    /// </summary>
    public static IReadOnlyList<string> HttpClientMeterNames { get; } = ["System.Net.Http", "System.Net.NameResolution"];

    /// <summary>
    /// Gets the activity sources governance instrumentation adds: AsiBackbone's governance emitter.
    /// </summary>
    public static IReadOnlyList<string> GovernanceActivitySourceNames { get; } =
        [OpenTelemetryGovernanceInstrumentation.ActivitySourceName];

    /// <summary>
    /// Gets the meters governance instrumentation adds: AsiBackbone's governance emitter and the host's audit
    /// reconciliation gauges.
    /// </summary>
    public static IReadOnlyList<string> GovernanceMeterNames { get; } =
        [OpenTelemetryGovernanceInstrumentation.MeterName, ApplicationAuditReconciliationMetrics.MeterName];

    /// <summary>
    /// Returns whether governance instrumentation is active: OpenTelemetry and governance instrumentation are enabled,
    /// and so is tracing or metrics.
    /// </summary>
    /// <param name="options">The OpenTelemetry options.</param>
    /// <returns><see langword="true" /> when governance evidence is projected onto telemetry.</returns>
    public static bool IsGovernanceInstrumentationActive(ApplicationOpenTelemetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Enabled &&
            options.EnableGovernanceInstrumentation &&
            (options.EnableTracing || options.EnableMetrics);
    }

    /// <summary>
    /// Returns whether the host's OpenTelemetry tracing collects an activity source.
    /// </summary>
    /// <param name="options">The OpenTelemetry options.</param>
    /// <param name="name">The activity source name.</param>
    /// <returns><see langword="true" /> when spans from the source are exported.</returns>
    public static bool IsActivitySourceCollected(ApplicationOpenTelemetryOptions options, string name)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Enabled && options.EnableTracing && (
            (options.EnableAspNetCoreInstrumentation &&
                string.Equals(name, AspNetCoreActivitySourceName, StringComparison.Ordinal)) ||
            (options.EnableHttpClientInstrumentation &&
                string.Equals(name, HttpClientActivitySourceName, StringComparison.Ordinal)) ||
            (options.EnableGovernanceInstrumentation &&
                GovernanceActivitySourceNames.Contains(name, StringComparer.Ordinal)));
    }

    /// <summary>
    /// Returns whether the host's OpenTelemetry metrics collect a meter.
    /// </summary>
    /// <param name="options">The OpenTelemetry options.</param>
    /// <param name="name">The meter name.</param>
    /// <returns><see langword="true" /> when the meter's instruments are exported.</returns>
    public static bool IsMeterCollected(ApplicationOpenTelemetryOptions options, string name)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Enabled && options.EnableMetrics && (
            (options.EnableAspNetCoreInstrumentation && AspNetCoreMeterNames.Contains(name, StringComparer.Ordinal)) ||
            (options.EnableHttpClientInstrumentation && HttpClientMeterNames.Contains(name, StringComparer.Ordinal)) ||
            (options.EnableGovernanceInstrumentation && GovernanceMeterNames.Contains(name, StringComparer.Ordinal)));
    }
}
