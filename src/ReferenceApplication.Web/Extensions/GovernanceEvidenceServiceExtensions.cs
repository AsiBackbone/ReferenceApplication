using AsiBackbone.Core.Audit;
using AsiBackbone.DependencyInjection;
using AsiBackbone.EntityFrameworkCore;
using ReferenceApplication.Infrastructure.Data;
using ReferenceApplication.Infrastructure.Data.Auditing;
using ReferenceApplication.Web.Governance;

namespace ReferenceApplication.Web.Extensions;

/// <summary>
/// Provides the Reference Application's governance evidence persistence composition (walkthrough Step 6).
/// </summary>
public static class GovernanceEvidenceServiceExtensions
{
    /// <summary>
    /// The configuration section that can override the NCAT audit-completion outbox options.
    /// </summary>
    public const string AuditCompletionOutboxSectionName = "ReferenceApplication:AuditCompletionOutbox";

    /// <summary>
    /// Persists governance decision receipts to the AsiBackbone audit ledger in the host-owned
    /// <see cref="ApplicationDbContext" />, and links NCAT audit completion to that evidence.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection so calls can be chained.</returns>
    /// <remarks>
    /// Call after <c>AddApplicationDataAccess</c> and <c>AddApplicationAsiBackbone</c>. The registration:
    /// <list type="bullet">
    /// <item><description>binds the AsiBackbone EF Core audit ledger and lifecycle stores to the host context;</description></item>
    /// <item><description>registers <see cref="LedgerDecisionReceiptSink" /> as the endpoint governance audit sink;</description></item>
    /// <item><description>exposes the request's persisted evidence through <see cref="IGovernanceDecisionEvidenceAccessor" />;</description></item>
    /// <item><description>enables the NCAT audit-completion outbox with <see cref="AsiBackboneAuditCompletionPublisher" /> as its default destination.</description></item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddApplicationGovernanceEvidence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAsiBackbone(builder => builder
            .UseEfCoreAuditLedger<ApplicationDbContext>()
            .UseEfCoreAuditLifecycle<ApplicationDbContext>());

        services.AddScoped<GovernanceDecisionEvidenceAccessor>();
        services.AddScoped<IGovernanceDecisionEvidenceAccessor>(serviceProvider =>
            serviceProvider.GetRequiredService<GovernanceDecisionEvidenceAccessor>());
        services.AddScoped<IDecisionReceiptSink, LedgerDecisionReceiptSink>();
        services.AddScoped<IGovernanceEvidenceQuery, GovernanceEvidenceQuery>();
        services.AddSingleton<IGovernanceEvidenceNotifier, InMemoryGovernanceEvidenceNotifier>();
        services.AddSingleton<GovernanceEvidenceNotificationPublisher>();

        services.AddScoped<IApplicationAuditCompletionPublisher, AsiBackboneAuditCompletionPublisher>();
        services.AddApplicationAuditCompletionOutbox(options =>
            options.DefaultDestination = AsiBackboneAuditCompletionPublisher.DestinationName);
        services.AddOptions<ApplicationAuditCompletionOutboxOptions>()
            .BindConfiguration(AuditCompletionOutboxSectionName);

        return services;
    }
}
