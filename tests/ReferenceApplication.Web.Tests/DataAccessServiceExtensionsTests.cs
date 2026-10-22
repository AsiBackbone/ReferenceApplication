using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReferenceApplication.Infrastructure.Data;
using ReferenceApplication.Infrastructure.Data.Auditing;
using ReferenceApplication.Infrastructure.Data.ExternalLogins;
using ReferenceApplication.Infrastructure.Data.Options;
using ReferenceApplication.Web.Extensions;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Provides tests for application data access provider configuration.
/// </summary>
public sealed class DataAccessServiceExtensionsTests
{
    /// <summary>
    /// Verifies that SQLite remains the default provider when no data access provider is explicitly configured.
    /// </summary>
    [Fact]
    public void AddApplicationDataAccess_DefaultConfiguration_UsesSqliteProvider()
    {
        IConfiguration configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:ApplicationDatabase"] = "Data Source=:memory:"
            });

        using ServiceProvider serviceProvider = CreateServiceProvider(configuration);

        using ApplicationDbContext context = serviceProvider.GetRequiredService<ApplicationDbContext>();

        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", context.Database.ProviderName);
    }

    /// <summary>
    /// Verifies that selecting SQL Server fails during service registration with an explicit unsupported message,
    /// because this reference application ships and tests a SQLite migration history only.
    /// </summary>
    [Theory]
    [InlineData("SqlServer")]
    [InlineData("sqlserver")]
    [InlineData("SQLSERVER")]
    [InlineData(" SqlServer ")]
    public void AddApplicationDataAccess_SqlServerProvider_ThrowsExplicitUnsupportedMessage(string provider)
    {
        IConfiguration configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:ApplicationSqlServer"] = "Server=(localdb)\\MSSQLLocalDB;Database=ApplicationApplicationTests;Trusted_Connection=True;TrustServerCertificate=True",
                ["ReferenceApplication:DataAccess:Provider"] = provider,
                ["ReferenceApplication:DataAccess:ConnectionStringName"] = "ApplicationSqlServer"
            });

        ServiceCollection services = new();
        services.AddLogging();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => services.AddApplicationDataAccess(configuration));

        Assert.Contains(
            "Data access provider 'SqlServer' is not supported",
            exception.Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "target SQLite only",
            exception.Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "your own SQL Server migration history",
            exception.Message,
            StringComparison.Ordinal);

        Assert.EndsWith(
            "Supported providers: Sqlite, None.",
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that provider matching is case-insensitive.
    /// </summary>
    [Theory]
    [InlineData("sqlite", "Microsoft.EntityFrameworkCore.Sqlite")]
    [InlineData("SQLITE", "Microsoft.EntityFrameworkCore.Sqlite")]
    public void AddApplicationDataAccess_ProviderNameMatching_IsCaseInsensitive(
        string provider,
        string expectedProviderName)
    {
        IConfiguration configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:ApplicationDatabase"] = "Data Source=:memory:",
                ["ReferenceApplication:DataAccess:Provider"] = provider,
                ["ReferenceApplication:DataAccess:ConnectionStringName"] = "ApplicationDatabase"
            });

        using ServiceProvider serviceProvider = CreateServiceProvider(configuration);

        using ApplicationDbContext context = serviceProvider.GetRequiredService<ApplicationDbContext>();

        Assert.Equal(expectedProviderName, context.Database.ProviderName);
    }

    /// <summary>
    /// Verifies that data access can be intentionally disabled without requiring EF Core services.
    /// </summary>
    [Theory]
    [InlineData("None")]
    [InlineData("none")]
    [InlineData("Disabled")]
    [InlineData("disabled")]
    public void AddApplicationDataAccess_DisabledProvider_SkipsEfCoreRegistrations(string provider)
    {
        IConfiguration configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["ReferenceApplication:DataAccess:Provider"] = provider
            });

        using ServiceProvider serviceProvider = CreateServiceProvider(configuration);

        Assert.Null(serviceProvider.GetService<ApplicationDbContext>());
        Assert.Null(serviceProvider.GetService<IDbContextFactory<ApplicationDbContext>>());
        Assert.Null(serviceProvider.GetService<IExternalLoginAccountResolver>());
    }

    /// <summary>
    /// Verifies that disabled data access does not require a configured connection string.
    /// </summary>
    [Fact]
    public void AddApplicationDataAccess_DisabledProvider_DoesNotRequireConnectionStringName()
    {
        IConfiguration configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["ReferenceApplication:DataAccess:Provider"] = "None",
                ["ReferenceApplication:DataAccess:ConnectionStringName"] = string.Empty
            });

        using ServiceProvider serviceProvider = CreateServiceProvider(configuration);

        DataAccessOptions options = serviceProvider
            .GetRequiredService<IOptions<DataAccessOptions>>()
            .Value;

        Assert.Equal("None", options.Provider);
        Assert.Null(serviceProvider.GetService<ApplicationDbContext>());
    }

    /// <summary>
    /// Verifies that blank provider values fail fast during service registration.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddApplicationDataAccess_BlankProvider_ThrowsInvalidOperationException(string provider)
    {
        IConfiguration configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:ApplicationDatabase"] = "Data Source=:memory:",
                ["ReferenceApplication:DataAccess:Provider"] = provider,
                ["ReferenceApplication:DataAccess:ConnectionStringName"] = "ApplicationDatabase"
            });

        ServiceCollection services = new();
        services.AddLogging();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => services.AddApplicationDataAccess(configuration));

        Assert.Contains(
            "Application data access provider was not configured.",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that blank connection string names fail fast during service registration.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddApplicationDataAccess_BlankConnectionStringName_ThrowsInvalidOperationException(
        string connectionStringName)
    {
        IConfiguration configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:ApplicationDatabase"] = "Data Source=:memory:",
                ["ReferenceApplication:DataAccess:Provider"] = "Sqlite",
                ["ReferenceApplication:DataAccess:ConnectionStringName"] = connectionStringName
            });

        ServiceCollection services = new();
        services.AddLogging();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => services.AddApplicationDataAccess(configuration));

        Assert.Contains(
            "Application data access connection string name was not configured.",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that unsupported provider names fail fast during service registration.
    /// </summary>
    [Fact]
    public void AddApplicationDataAccess_UnsupportedProvider_ThrowsInvalidOperationException()
    {
        IConfiguration configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:ApplicationDatabase"] = "Data Source=:memory:",
                ["ReferenceApplication:DataAccess:Provider"] = "PostgreSql",
                ["ReferenceApplication:DataAccess:ConnectionStringName"] = "ApplicationDatabase"
            });

        ServiceCollection services = new();
        services.AddLogging();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => services.AddApplicationDataAccess(configuration));

        Assert.Equal(
            "Unsupported data access provider 'PostgreSql'. Supported providers: Sqlite, None.",
            exception.Message);
    }

    /// <summary>
    /// Verifies the supported-provider policy: SQLite and the disabled aliases only.
    /// </summary>
    [Theory]
    [InlineData("Sqlite", true)]
    [InlineData(" sqlite ", true)]
    [InlineData("None", true)]
    [InlineData("Disabled", true)]
    [InlineData("SqlServer", false)]
    [InlineData("PostgreSql", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void DataAccessOptions_IsSupportedProvider_MatchesSupportPolicy(string? provider, bool expected)
    {
        Assert.Equal(expected, DataAccessOptions.IsSupportedProvider(provider));
    }

    /// <summary>
    /// Verifies that a configured provider fails fast when its configured connection string is missing.
    /// </summary>
    [Fact]
    public void AddApplicationDataAccess_MissingConnectionString_ThrowsInvalidOperationException()
    {
        IConfiguration configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["ReferenceApplication:DataAccess:Provider"] = "Sqlite",
                ["ReferenceApplication:DataAccess:ConnectionStringName"] = "MissingDatabase"
            });

        ServiceCollection services = new();
        services.AddLogging();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => services.AddApplicationDataAccess(configuration));

        Assert.Contains(
            "Connection string 'MissingDatabase' was not configured.",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that EF Core audit record creation is enabled when configured.
    /// </summary>
    [Fact]
    public void AddApplicationDataAccess_AuditingEnabled_BindsAuditOptions()
    {
        IConfiguration configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:ApplicationDatabase"] = "Data Source=:memory:",
                ["ReferenceApplication:DataAccess:Provider"] = "Sqlite",
                ["ReferenceApplication:DataAccess:ConnectionStringName"] = "ApplicationDatabase",
                ["ReferenceApplication:DataAccess:Auditing:Enabled"] = "true"
            });

        using ServiceProvider serviceProvider = CreateServiceProvider(configuration);

        DataAccessOptions options = serviceProvider
            .GetRequiredService<IOptions<DataAccessOptions>>()
            .Value;

        Assert.True(options.Auditing.Enabled);
    }

    /// <summary>
    /// Verifies that EF Core audit record creation can be disabled through configuration.
    /// </summary>
    [Fact]
    public void AddApplicationDataAccess_AuditingDisabled_BindsAuditOptions()
    {
        IConfiguration configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:ApplicationDatabase"] = "Data Source=:memory:",
                ["ReferenceApplication:DataAccess:Provider"] = "Sqlite",
                ["ReferenceApplication:DataAccess:ConnectionStringName"] = "ApplicationDatabase",
                ["ReferenceApplication:DataAccess:Auditing:Enabled"] = "false"
            });

        using ServiceProvider serviceProvider = CreateServiceProvider(configuration);

        DataAccessOptions options = serviceProvider
            .GetRequiredService<IOptions<DataAccessOptions>>()
            .Value;

        Assert.False(options.Auditing.Enabled);
    }

    /// <summary>
    /// Verifies that EF Core audit record creation is enabled by default when not explicitly configured.
    /// </summary>
    [Fact]
    public void AddApplicationDataAccess_AuditingNotConfigured_DefaultsToEnabled()
    {
        IConfiguration configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:ApplicationDatabase"] = "Data Source=:memory:"
            });

        using ServiceProvider serviceProvider = CreateServiceProvider(configuration);

        DataAccessOptions options = serviceProvider
            .GetRequiredService<IOptions<DataAccessOptions>>()
            .Value;

        Assert.True(options.Auditing.Enabled);
    }

    /// <summary>
    /// Verifies that local audit storage is the default storage mode.
    /// </summary>
    [Fact]
    public void AddApplicationDataAccess_AuditStorageModeNotConfigured_DefaultsToLocal()
    {
        IConfiguration configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:ApplicationDatabase"] = "Data Source=:memory:"
            });

        using ServiceProvider serviceProvider = CreateServiceProvider(configuration);

        DataAccessOptions options = serviceProvider
            .GetRequiredService<IOptions<DataAccessOptions>>()
            .Value;

        Assert.Equal(AuditStorageModes.Local, options.Auditing.StorageMode);
        Assert.IsType<LocalApplicationAuditStore>(serviceProvider.GetRequiredService<IApplicationAuditStore>());
    }

    /// <summary>
    /// Verifies that non-local audit storage modes bind for consuming applications that provide a custom store.
    /// </summary>
    [Fact]
    public void AddApplicationDataAccess_AuditStorageModeConfigured_BindsAuditOptions()
    {
        IConfiguration configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:ApplicationDatabase"] = "Data Source=:memory:",
                ["ReferenceApplication:DataAccess:Auditing:StorageMode"] = AuditStorageModes.ExternalSink
            });

        using ServiceProvider serviceProvider = CreateServiceProvider(configuration);

        DataAccessOptions options = serviceProvider
            .GetRequiredService<IOptions<DataAccessOptions>>()
            .Value;

        Assert.Equal(AuditStorageModes.ExternalSink, options.Auditing.StorageMode);
        Assert.Null(serviceProvider.GetService<IApplicationAuditStore>());
    }

    /// <summary>
    /// Verifies that unsupported audit storage modes fail fast.
    /// </summary>
    [Fact]
    public void AddApplicationDataAccess_UnsupportedAuditStorageMode_ThrowsInvalidOperationException()
    {
        IConfiguration configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:ApplicationDatabase"] = "Data Source=:memory:",
                ["ReferenceApplication:DataAccess:Provider"] = "Sqlite",
                ["ReferenceApplication:DataAccess:ConnectionStringName"] = "ApplicationDatabase",
                ["ReferenceApplication:DataAccess:Auditing:StorageMode"] = "Unknown"
            });

        ServiceCollection services = new();
        services.AddLogging();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => services.AddApplicationDataAccess(configuration));

        Assert.Contains(
            "audit storage mode",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static ServiceProvider CreateServiceProvider(IConfiguration configuration)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddApplicationDataAccess(configuration);

        return services.BuildServiceProvider();
    }

    private static IConfiguration CreateConfiguration(
        IReadOnlyDictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
