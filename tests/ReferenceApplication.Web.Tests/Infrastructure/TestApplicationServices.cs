using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReferenceApplication.Web.Extensions;

namespace ReferenceApplication.Web.Tests.Infrastructure;

/// <summary>
/// Builds the application's data access and governance evidence registrations against a temporary, migrated SQLite
/// database, without starting the web host.
/// </summary>
internal sealed class TestApplicationServices : IAsyncDisposable
{
    private readonly TestSqliteDatabase _database;

    private TestApplicationServices(TestSqliteDatabase database, ServiceProvider services)
    {
        _database = database;
        Services = services;
    }

    /// <summary>
    /// Gets the application service provider.
    /// </summary>
    public ServiceProvider Services { get; }

    /// <summary>
    /// Creates the services and, by default, applies the application's migrations.
    /// </summary>
    /// <param name="migrate">Whether to create the schema. Pass <see langword="false" /> to simulate a database
    /// that cannot store governance evidence.</param>
    /// <param name="auditingEnabled">Whether NCAT mutation auditing is enabled.</param>
    /// <returns>The services.</returns>
    public static async Task<TestApplicationServices> CreateAsync(bool migrate = true, bool auditingEnabled = true)
    {
        var database = TestSqliteDatabase.Create();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReferenceApplication:DataAccess:Provider"] = "Sqlite",
                ["ReferenceApplication:DataAccess:ConnectionStringName"] = "ApplicationDatabase",
                ["ReferenceApplication:DataAccess:Auditing:Enabled"] = auditingEnabled ? "true" : "false",
                ["ConnectionStrings:ApplicationDatabase"] = database.ConnectionString
            })
            .Build();

        ServiceCollection services = new();
        _ = services.AddSingleton(configuration);
        _ = services.AddLogging();
        _ = services.AddApplicationDataAccess(configuration);
        _ = services.AddApplicationGovernanceEvidence();

        ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        if (migrate)
        {
            await TestSqliteDatabase.MigrateAsync(provider).ConfigureAwait(false);
        }

        return new TestApplicationServices(database, provider);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync().ConfigureAwait(false);
        _database.Dispose();
    }
}
