using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReferenceApplication.Infrastructure.Data;
using ReferenceApplication.Infrastructure.Data.Extensions;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests.Upgrades;

/// <summary>
/// Verifies release-to-release database upgrades from the oldest supported release,
/// <see cref="ReleaseUpgradeEvidence.BoundaryRelease" />: a database holding that release's representative evidence is
/// upgraded by the current migrations without losing or changing any row, and the upgraded application becomes ready
/// and still verifies and links every piece of evidence.
/// </summary>
/// <remarks>
/// The boundary database is recreated from the shipped migrations up to <see cref="ReleaseUpgradeEvidence.BoundaryMigration" />,
/// which <see cref="ShippedMigrations_AreUnchanged" /> proves are byte-for-byte the ones the release shipped, and the
/// evidence captured from that release in <c>evidence.sql</c>. CI also upgrades a database created by the release's own
/// migration bundle with the current bundle and runs <see cref="DeployedUpgrade_PreservesTheBoundaryEvidence" />
/// against the result.
/// </remarks>
public sealed class ReleaseUpgradeTests : IDisposable
{
    /// <summary>
    /// The environment variable that names a database the current migration bundle upgraded from the boundary release.
    /// </summary>
    public const string UpgradedDatabaseVariable = "REFERENCEAPPLICATION_UPGRADED_DATABASE";

    /// <summary>
    /// The environment variable that names where to write a regenerated fixture.
    /// </summary>
    public const string GenerateFixtureVariable = "REFERENCEAPPLICATION_GENERATE_UPGRADE_FIXTURE";

    private readonly List<TestSqliteDatabase> _databases = [];

    /// <summary>
    /// Verifies that every migration the boundary release shipped is unchanged, and that no migration has been inserted
    /// before the boundary. Shipped migrations are immutable: a change to the schema needs a new migration.
    /// </summary>
    [Fact]
    public void ShippedMigrations_AreUnchanged()
    {
        var manifest = RepositoryFiles.ReadAllText(ReleaseUpgradeEvidence.MigrationManifestPath)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => !line.StartsWith('#'))
            .Select(static line => line.Split("  ", 2))
            .ToDictionary(static parts => parts[1], static parts => parts[0], StringComparer.Ordinal);

        string[] shipped = [.. ShippedMigrationFiles()];
        Assert.Equal(manifest.Keys.Order(StringComparer.Ordinal), shipped);
        foreach (string file in shipped)
        {
            Assert.True(
                manifest[file] == HashFile(file),
                $"{file} changed after {ReleaseUpgradeEvidence.BoundaryRelease} shipped it. Add a new migration instead.");
        }
    }

    /// <summary>
    /// Verifies that the CI release-upgrade job starts from the same boundary release and fixture as these tests.
    /// </summary>
    [Fact]
    public void ContinuousIntegration_UpgradesFromTheSameBoundary()
    {
        string workflow = RepositoryFiles.ReadAllText(".github/workflows/ci.yml");

        Assert.Contains($"PREVIOUS_RELEASE: {ReleaseUpgradeEvidence.BoundaryRelease}\n", workflow, StringComparison.Ordinal);
        Assert.Contains($"FIXTURE: {ReleaseUpgradeEvidence.FixturePath}\n", workflow, StringComparison.Ordinal);
        Assert.Contains("ReleaseUpgradeTests.DeployedUpgrade_PreservesTheBoundaryEvidence", workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that upgrading a boundary database keeps every row unchanged, that a second upgrade changes nothing, that
    /// the upgraded schema matches a clean install, and that the upgraded application becomes ready and still verifies
    /// and links its evidence.
    /// </summary>
    [Fact]
    public async Task Upgrade_FromTheBoundaryRelease_PreservesEveryRowAndKeepsEvidenceVerifiable()
    {
        TestSqliteDatabase database = CreateDatabase();
        Dictionary<string, Dictionary<string, Dictionary<string, object?>>> before = await LoadBoundaryAsync(database);

        await MigrateAsync(database, target: null);
        Dictionary<string, Dictionary<string, Dictionary<string, object?>>> after = ReleaseUpgradeEvidence.Snapshot(database.FilePath);
        Assert.Empty(ReleaseUpgradeEvidence.CompareSnapshots(before, after));
        Assert.Empty(await PendingMigrationsAsync(database));

        // A second application of the same migrations is a no-op.
        await MigrateAsync(database, target: null);
        Dictionary<string, Dictionary<string, Dictionary<string, object?>>> again = ReleaseUpgradeEvidence.Snapshot(database.FilePath);
        Assert.Empty(ReleaseUpgradeEvidence.CompareSnapshots(after, again));
        Assert.Empty(ReleaseUpgradeEvidence.CompareSnapshots(again, after));

        TestSqliteDatabase clean = CreateDatabase();
        await MigrateAsync(clean, target: null);
        Assert.Equal(ReleaseUpgradeEvidence.ReadSchema(clean.FilePath), ReleaseUpgradeEvidence.ReadSchema(database.FilePath));

        using WebApplicationFactory<Program> host = await ReleaseUpgradeEvidence.CreateHostAsync(database);
        Assert.Empty(await ReleaseUpgradeEvidence.VerifyEvidenceAsync(host, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies that the checks catch a destructive or incompatible migration: lost rows, changed values that break
    /// record hashes, and dropped columns are each reported.
    /// </summary>
    /// <param name="destructiveChange">SQL standing in for a destructive migration.</param>
    /// <param name="expectedProblems">Text each expected problem report contains.</param>
    [Theory]
    [InlineData(
        "DELETE FROM \"AsiBackboneAuditResidueLifecycleEvents\";",
        new[] { "Row AsiBackboneAuditResidueLifecycleEvents[", "has lost its lifecycle event", "has lost its lifecycle-event evidence" })]
    [InlineData(
        "UPDATE \"AsiBackboneAuditLedgerRecords\" SET \"OperationName\" = \"OperationName\" || ' (changed)';",
        new[] { ".OperationName changed during the upgrade", "no longer verifies: mismatch" })]
    [InlineData(
        "ALTER TABLE \"AsiBackboneCapabilityGrantUses\" DROP COLUMN \"UseCount\";",
        new[] { "Column AsiBackboneCapabilityGrantUses.UseCount is missing after the upgrade." })]
    [InlineData(
        "DELETE FROM \"AsiBackboneCapabilityGrantUses\";",
        new[] { "Row AsiBackboneCapabilityGrantUses[", "has lost its use record" })]
    public async Task Upgrade_DetectsADestructiveMigration(string destructiveChange, string[] expectedProblems)
    {
        ArgumentNullException.ThrowIfNull(expectedProblems);

        TestSqliteDatabase database = CreateDatabase();
        Dictionary<string, Dictionary<string, Dictionary<string, object?>>> before = await LoadBoundaryAsync(database);

        ReleaseUpgradeEvidence.Execute(database.FilePath, destructiveChange);
        await MigrateAsync(database, target: null);

        List<string> problems = ReleaseUpgradeEvidence.CompareSnapshots(before, ReleaseUpgradeEvidence.Snapshot(database.FilePath));
        using (WebApplicationFactory<Program> host = await ReleaseUpgradeEvidence.CreateHostAsync(database))
        {
            problems.AddRange(await ReleaseUpgradeEvidence.VerifyEvidenceAsync(host, TestContext.Current.CancellationToken));
        }

        foreach (string expected in expectedProblems)
        {
            Assert.True(
                problems.Any(problem => problem.Contains(expected, StringComparison.Ordinal)),
                $"Expected a problem containing \"{expected}\". Reported:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
        }
    }

    /// <summary>
    /// Verifies a database that CI created with the boundary release's own migration bundle, seeded with the boundary
    /// evidence, and upgraded with the current bundle: every row survived unchanged, nothing is pending, and the
    /// application still verifies and links the evidence. Runs only when <see cref="UpgradedDatabaseVariable" /> is set.
    /// </summary>
    [Fact]
    public async Task DeployedUpgrade_PreservesTheBoundaryEvidence()
    {
        string? deployed = Environment.GetEnvironmentVariable(UpgradedDatabaseVariable);
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(deployed),
            $"Set {UpgradedDatabaseVariable} to a database upgraded by the current migration bundle; CI does this.");

        TestSqliteDatabase expected = CreateDatabase();
        Dictionary<string, Dictionary<string, Dictionary<string, object?>>> before = await LoadBoundaryAsync(expected);

        // Work on a copy, so the deployed file stays as CI left it.
        TestSqliteDatabase database = CreateDatabase();
        File.Copy(deployed, database.FilePath);

        Assert.Empty(ReleaseUpgradeEvidence.CompareSnapshots(before, ReleaseUpgradeEvidence.Snapshot(database.FilePath)));
        Assert.Empty(await PendingMigrationsAsync(database));
        using WebApplicationFactory<Program> host = await ReleaseUpgradeEvidence.CreateHostAsync(database);
        Assert.Empty(await ReleaseUpgradeEvidence.VerifyEvidenceAsync(host, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Regenerates the boundary fixture through the application's own paths. Run it only on the boundary release, with
    /// <see cref="GenerateFixtureVariable" /> naming the output file; see the fixture directory's README.
    /// </summary>
    [Fact]
    public async Task GenerateBoundaryFixture()
    {
        string? output = Environment.GetEnvironmentVariable(GenerateFixtureVariable);
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(output),
            $"Set {GenerateFixtureVariable} to an output path to regenerate the upgrade fixture.");

        TestSqliteDatabase database = CreateDatabase();
        await MigrateAsync(database, ReleaseUpgradeEvidence.BoundaryMigration);
        Assert.True(
            (await PendingMigrationsAsync(database)).Count == 0,
            $"Generate the fixture on {ReleaseUpgradeEvidence.BoundaryRelease}, whose last migration is the boundary.");

        await ReleaseUpgradeEvidence.ProduceAsync(database, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            output,
            ReleaseUpgradeEvidence.Capture(database.FilePath),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            TestContext.Current.CancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (TestSqliteDatabase database in _databases)
        {
            database.Dispose();
        }
    }

    private TestSqliteDatabase CreateDatabase()
    {
        var database = TestSqliteDatabase.Create();
        _databases.Add(database);
        return database;
    }

    private static async Task<Dictionary<string, Dictionary<string, Dictionary<string, object?>>>> LoadBoundaryAsync(
        TestSqliteDatabase database)
    {
        await MigrateAsync(database, ReleaseUpgradeEvidence.BoundaryMigration);
        ReleaseUpgradeEvidence.Execute(database.FilePath, RepositoryFiles.ReadAllText(ReleaseUpgradeEvidence.FixturePath));
        return ReleaseUpgradeEvidence.Snapshot(database.FilePath);
    }

    private static async Task MigrateAsync(TestSqliteDatabase database, string? target)
    {
        await using ApplicationDbContext dbContext = CreateDbContext(database);
        await dbContext.GetService<IMigrator>().MigrateAsync(target, TestContext.Current.CancellationToken);
    }

    private static async Task<List<string>> PendingMigrationsAsync(TestSqliteDatabase database)
    {
        await using ApplicationDbContext dbContext = CreateDbContext(database);
        return [.. await dbContext.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken)];
    }

    private static ApplicationDbContext CreateDbContext(TestSqliteDatabase database)
    {
        // The application's own data-access composition, outside any host, so migrating never races a host's
        // background workers.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReferenceApplication:DataAccess:Provider"] = "Sqlite",
                ["ReferenceApplication:DataAccess:ConnectionStringName"] = "ApplicationDatabase",
                ["ConnectionStrings:ApplicationDatabase"] = database.ConnectionString
            })
            .Build();
        ServiceCollection services = new();
        _ = services.AddLogging();
        _ = services.AddApplicationInfrastructureDataAccess(configuration);
        return services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<ApplicationDbContext>();
    }

    private static IEnumerable<string> ShippedMigrationFiles()
    {
        // Migration files are named for their identifier, which starts with a 14-digit timestamp; a migration added
        // after the boundary release always sorts after it.
        const string directory = "src/ReferenceApplication.Infrastructure/Data/Migrations";
        const int timestampLength = 14;
        string boundaryTimestamp = ReleaseUpgradeEvidence.BoundaryMigration[..timestampLength];
        return Directory.GetFiles(RepositoryFiles.GetPath(directory), "*.cs")
            .Select(static path => Path.GetFileName(path))
            .Where(static name => !name.StartsWith("ApplicationDbContextModelSnapshot", StringComparison.Ordinal))
            .Where(name => string.CompareOrdinal(name[..timestampLength], boundaryTimestamp) <= 0)
            .Select(static name => $"{directory}/{name}")
            .Order(StringComparer.Ordinal);
    }

    private static string HashFile(string relativePath)
    {
        // Line endings are normalized, so a checkout's line-ending conversion does not count as a change.
        byte[] content = Encoding.UTF8.GetBytes(RepositoryFiles.ReadAllText(relativePath));
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }
}
