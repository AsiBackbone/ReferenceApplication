using ReferenceApplication.Infrastructure.Data.Auditing;
using ReferenceApplication.Web.Authentication.Extensions;
using ReferenceApplication.Web.Governance.Pipeline;

namespace ReferenceApplication.Web.Extensions;

/// <summary>
/// Registers and maps the explicitly enabled, Development-only pipeline inspector.
/// </summary>
public static class PipelineInspectorServiceExtensions
{
    /// <summary>
    /// Gets the route that returns a pipeline snapshot.
    /// </summary>
    public const string SnapshotPath = "/api/v1/pipeline-inspector/snapshot";

    /// <summary>
    /// Registers the inspector only when its switch is enabled in Development.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="environment">The host environment.</param>
    /// <returns>The same service collection so calls can be chained.</returns>
    /// <remarks>
    /// <para>
    /// Call after <c>AddApplicationOpenTelemetry</c> and <c>AddApplicationGovernanceEvidence</c>: the inspector reads
    /// the audit-completion outbox that the evidence composition registers, and observes the telemetry that
    /// OpenTelemetry creates.
    /// </para>
    /// <para>
    /// The host does not otherwise register NCAT audit reconciliation. While the inspector is active, this also
    /// registers it, bound to <see cref="PipelineInspectorOptions.ReconciliationSectionName" />, with its background
    /// worker, metrics, and audit-integrity health check, so the inspector has a summary and findings to show.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddApplicationPipelineInspector(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        if (!IsActive(configuration, environment))
        {
            return services;
        }

        services.AddApplicationAuditReconciliation();
        services.AddOptions<ApplicationAuditReconciliationOptions>()
            .BindConfiguration(PipelineInspectorOptions.ReconciliationSectionName);

        services.AddSingleton<PipelineTelemetryRecorder>();
        services.AddHostedService(static serviceProvider =>
            serviceProvider.GetRequiredService<PipelineTelemetryRecorder>());
        services.AddScoped<IPipelineInspector, PipelineInspector>();
        return services;
    }

    /// <summary>
    /// Maps the administrator-only snapshot route only when the inspector is active in Development.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <returns>The same application so calls can be chained.</returns>
    public static WebApplication MapApplicationPipelineInspector(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!IsActive(app.Configuration, app.Environment) ||
            app.Services.GetService<IServiceProviderIsService>()?.IsService(typeof(IPipelineInspector)) != true)
        {
            return app;
        }

        app.MapGet(SnapshotPath, GetSnapshotAsync)
            .RequireAuthorization(ApplicationAuthorizationPolicyNames.AdministratorRole)
            .WithDisplayName("Read the governance pipeline");
        return app;
    }

    /// <summary>
    /// Determines whether the feature switch is enabled in the Development environment.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="environment">The host environment.</param>
    /// <returns><see langword="true" /> when the inspector is active.</returns>
    public static bool IsActive(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        return environment.IsDevelopment() && configuration.GetValue<bool>(
            $"{PipelineInspectorOptions.SectionName}:{nameof(PipelineInspectorOptions.Enabled)}");
    }

    private static async Task<IResult> GetSnapshotAsync(
        string? correlationId,
        string? status,
        HttpContext httpContext,
        IPipelineInspector inspector,
        CancellationToken cancellationToken)
    {
        if (correlationId is { Length: > PipelineInspectorOptions.MaximumCorrelationIdLength })
        {
            return Results.BadRequest(new { message = "The correlation identifier is too long." });
        }

        if (!string.IsNullOrWhiteSpace(status) &&
            !inspector.OutboxStatuses.Contains(status.Trim(), StringComparer.Ordinal))
        {
            return Results.BadRequest(new { message = $"Choose one of the outbox statuses: {string.Join(", ", inspector.OutboxStatuses)}." });
        }

        // A snapshot is polled while the inspector is open, so it must never be served from a cache.
        httpContext.Response.Headers.CacheControl = "no-store";
        PipelineInspectorSnapshot snapshot = await inspector
            .GetSnapshotAsync(correlationId, status, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(snapshot);
    }
}
