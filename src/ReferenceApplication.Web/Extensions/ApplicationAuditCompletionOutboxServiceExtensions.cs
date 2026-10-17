using ReferenceApplication.Infrastructure.Data.Auditing;
using ReferenceApplication.Infrastructure.Data.Extensions;
using ReferenceApplication.Web.Services;

namespace ReferenceApplication.Web.Extensions;

/// <summary>
/// Provides opt-in web-host registration for durable audit-completion delivery.
/// </summary>
public static class ApplicationAuditCompletionOutboxServiceExtensions
{
    /// <summary>
    /// Registers the audited transaction coordinator, durable local outbox, and hosted dispatcher.
    /// </summary>
    public static IServiceCollection AddApplicationAuditCompletionOutbox(
        this IServiceCollection services,
        Action<ApplicationAuditCompletionOutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddApplicationAuditedTransactions();
        services.AddApplicationAuditCompletionOutboxCore(configure);
        services.AddHostedService<ApplicationAuditCompletionOutboxHostedService>();
        return services;
    }
}
