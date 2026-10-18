using ReferenceApplication.Infrastructure.Data.Auditing;
using ReferenceApplication.Infrastructure.Data.Extensions;
using ReferenceApplication.Web.HealthChecks;
using ReferenceApplication.Web.Services;

namespace ReferenceApplication.Web.Extensions;

public static class ApplicationAuditReconciliationServiceExtensions
{
    public static IServiceCollection AddApplicationAuditReconciliation(
        this IServiceCollection services,
        Action<ApplicationAuditReconciliationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddApplicationAuditReconciliationCore(configure);
        services.AddHostedService<ApplicationAuditReconciliationHostedService>();
        services.AddHealthChecks()
            .AddCheck<ApplicationAuditIntegrityHealthCheck>(
                "application-audit-integrity",
                tags: [ApplicationHealthCheckTags.Audit, ApplicationHealthCheckTags.Integrity]);
        return services;
    }
}
