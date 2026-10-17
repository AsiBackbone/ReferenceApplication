using ReferenceApplication.Infrastructure.Data;
using ReferenceApplication.Infrastructure.Data.Auditing;
using ReferenceApplication.Infrastructure.Data.Extensions;
using ReferenceApplication.Infrastructure.Data.Services;
using ReferenceApplication.Web.Accessors;
using ReferenceApplication.Web.HealthChecks;
using ReferenceApplication.Web.Options;

namespace ReferenceApplication.Web.Extensions;

/// <summary>
/// Provides service registration methods for application data access.
/// </summary>
public static class DataAccessServiceExtensions
{
    /// <summary>
    /// Registers EF Core data access services for the web application.
    /// </summary>
    public static IServiceCollection AddApplicationDataAccess(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentActorAccessor, HttpContextCurrentActorAccessor>();
        services.AddScoped<IApplicationAuditContextAccessor, HttpContextApplicationAuditContextAccessor>();

        services.AddApplicationInfrastructureDataAccess(configuration);
        AddApplicationDatabaseReadinessCheck(services, configuration);

        services.AddHostedService<DataAccessStartupLogger>();

        return services;
    }

    private static void AddApplicationDatabaseReadinessCheck(
        IServiceCollection services,
        IConfiguration configuration)
    {
        // Bound lazily and read by the check on every run, so the setting follows the final configuration, including
        // sources added after service registration such as test or orchestrator overrides.
        services
            .AddOptions<ApplicationHealthCheckOptions>()
            .Bind(configuration.GetSection(ApplicationHealthCheckOptions.SectionName));

        // Infrastructure registers ApplicationDbContext only when a data access provider is enabled.
        bool dataAccessEnabled = services.Any(descriptor => descriptor.ServiceType == typeof(ApplicationDbContext));

        if (!dataAccessEnabled)
        {
            return;
        }

        services.AddHealthChecks()
            .AddCheck<ApplicationDatabaseHealthCheck>(
                "application-database",
                tags: [ApplicationHealthCheckTags.Ready, ApplicationHealthCheckTags.Database]);
    }
}
