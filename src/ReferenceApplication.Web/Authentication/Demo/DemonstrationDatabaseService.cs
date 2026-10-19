using Microsoft.EntityFrameworkCore;
using ReferenceApplication.Infrastructure.Data;

namespace ReferenceApplication.Web.Authentication.Demo;

/// <summary>
/// Migrates and resets the disposable Development governance demonstration database.
/// </summary>
internal sealed class DemonstrationDatabaseService(
    IDbContextFactory<ApplicationDbContext> dbContextFactory) : IDemonstrationDatabaseService
{
    private static readonly SemaphoreSlim _databaseGate = new(1, 1);
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory =
        dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));

    /// <inheritdoc />
    public Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteExclusiveAsync(reset: false, cancellationToken);
    }

    /// <inheritdoc />
    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteExclusiveAsync(reset: true, cancellationToken);
    }

    private async Task ExecuteExclusiveAsync(bool reset, CancellationToken cancellationToken)
    {
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using ApplicationDbContext dbContext = await _dbContextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);

            if (reset)
            {
                _ = await dbContext.Database.EnsureDeletedAsync(cancellationToken).ConfigureAwait(false);
            }

            await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _databaseGate.Release();
        }
    }
}
