namespace ReferenceApplication.Web.Authentication.Demo;

/// <summary>
/// Manages the disposable SQLite database used only by the Development governance demonstration.
/// </summary>
public interface IDemonstrationDatabaseService
{
    /// <summary>
    /// Applies the application's migration history without removing existing demonstration evidence.
    /// </summary>
    /// <param name="cancellationToken">Stops the operation when cancellation is requested.</param>
    /// <returns>A task that completes when the schema is current.</returns>
    Task MigrateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the disposable demonstration database and recreates its schema from migrations.
    /// </summary>
    /// <param name="cancellationToken">Stops the operation when cancellation is requested.</param>
    /// <returns>A task that completes when the empty schema is ready.</returns>
    Task ResetAsync(CancellationToken cancellationToken = default);
}
