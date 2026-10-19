using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReferenceApplication.Infrastructure.Data;

namespace ReferenceApplication.Web.Tests.Infrastructure;

/// <summary>
/// Provides a temporary SQLite database file for web host tests that persist data, migrated with the application's
/// real EF Core migrations.
/// </summary>
/// <remarks>
/// The application resolves its connection string while registering services, before test configuration sources are
/// applied, so the database is supplied through host settings (<c>UseSetting</c>), which are available during
/// registration. Applying the real migration history also verifies that the migrations, including the walkthrough
/// Step 6 AsiBackbone persistence migration, apply cleanly to an empty database.
/// </remarks>
internal sealed class TestSqliteDatabase : IDisposable
{
    private TestSqliteDatabase(string path)
    {
        FilePath = path;
    }

    /// <summary>
    /// Gets the database file path.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Gets the connection string for the database file. Pooling is disabled so the file can be deleted afterwards.
    /// </summary>
    public string ConnectionString => $"Data Source={FilePath};Pooling=False";

    /// <summary>
    /// Creates a new, empty database file location.
    /// </summary>
    /// <returns>The temporary database.</returns>
    public static TestSqliteDatabase Create()
    {
        return new TestSqliteDatabase(Path.Combine(Path.GetTempPath(), $"referenceapplication-tests-{Guid.NewGuid():N}.db"));
    }

    /// <summary>
    /// Points a web application factory at this database and creates the schema from the real migrations.
    /// </summary>
    /// <param name="factory">The factory to configure.</param>
    /// <param name="migrate">Whether to create the schema. Pass <see langword="false" /> to simulate a database
    /// that cannot store governance evidence.</param>
    /// <returns>The configured factory, with the database migrated unless disabled.</returns>
    public async Task<WebApplicationFactory<Program>> ApplyAsync(WebApplicationFactory<Program> factory, bool migrate = true)
    {
        ArgumentNullException.ThrowIfNull(factory);

        WebApplicationFactory<Program> configured = factory.WithWebHostBuilder(builder => builder
            .UseSetting("ReferenceApplication:DataAccess:Provider", "Sqlite")
            .UseSetting("ReferenceApplication:DataAccess:ConnectionStringName", "ApplicationDatabase")
            .UseSetting("ConnectionStrings:ApplicationDatabase", ConnectionString)
            .UseSetting("ConnectionStrings:DemonstrationDatabase", ConnectionString)
            // Tests dispatch the audit-completion outbox explicitly; keep the background loop out of the way.
            .UseSetting("ReferenceApplication:AuditCompletionOutbox:PollInterval", "01:00:00")
            .UseSetting("ReferenceApplication:AuditCompletionOutbox:MaximumCycleRetryDelay", "01:00:00"));

        if (migrate)
        {
            await MigrateAsync(configured.Services).ConfigureAwait(false);
        }

        return configured;
    }

    /// <summary>
    /// Applies the application's migrations through the given service provider.
    /// </summary>
    /// <param name="services">The application service provider.</param>
    /// <returns>A task that completes when the database is migrated.</returns>
    public static async Task MigrateAsync(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await dbContext.Database.MigrateAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (string path in new[] { FilePath, $"{FilePath}-wal", $"{FilePath}-shm", $"{FilePath}-journal" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // A lingering handle only leaves a temporary file behind; it must not fail the test.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
