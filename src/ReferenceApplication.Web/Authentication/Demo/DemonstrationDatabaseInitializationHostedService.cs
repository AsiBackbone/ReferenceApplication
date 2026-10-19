namespace ReferenceApplication.Web.Authentication.Demo;

/// <summary>
/// Applies migrations to the disposable Development demonstration database before the web host accepts requests.
/// </summary>
internal sealed class DemonstrationDatabaseInitializationHostedService(
    IServiceScopeFactory scopeFactory) : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IDemonstrationDatabaseService database = scope.ServiceProvider
            .GetRequiredService<IDemonstrationDatabaseService>();
        await database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
