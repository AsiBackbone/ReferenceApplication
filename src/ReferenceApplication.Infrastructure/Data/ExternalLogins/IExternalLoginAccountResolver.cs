using ReferenceApplication.Infrastructure.Data.Entities;

namespace ReferenceApplication.Infrastructure.Data.ExternalLogins;

/// <summary>
/// Resolves persisted external login account links.
/// </summary>
public interface IExternalLoginAccountResolver
{
    Task<ExternalLoginAccount?> FindByProviderUserIdAsync(
        string providerName,
        string providerUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExternalLoginAccount>> FindByLocalUserIdAsync(
        Guid localUserId,
        CancellationToken cancellationToken = default);
}
