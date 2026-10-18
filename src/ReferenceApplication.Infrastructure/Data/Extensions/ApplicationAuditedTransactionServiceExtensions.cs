using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReferenceApplication.Infrastructure.Data.Auditing;

namespace ReferenceApplication.Infrastructure.Data.Extensions;

/// <summary>
/// Provides opt-in service registration for audited application transaction coordination.
/// </summary>
public static class ApplicationAuditedTransactionServiceExtensions
{
    /// <summary>
    /// Registers the provider-neutral audited transaction coordinator.
    /// </summary>
    /// <remarks>
    /// Call <see cref="InfrastructureDataAccessServiceExtensions" />
    /// before this method so the application database context and audit receipt accessor are available.
    /// </remarks>
    public static IServiceCollection AddApplicationAuditedTransactions(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IApplicationAuditedTransaction, ApplicationAuditedTransaction>();
        return services;
    }
}
