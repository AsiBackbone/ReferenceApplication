
namespace ReferenceApplication.Infrastructure.Data.Options;

public sealed class DataAccessOptions
{
    public const string SectionName = "ReferenceApplication:DataAccess";

    /// <summary>
    /// The only database provider this reference application supports. The shipped EF Core migration history,
    /// migration bundle, and persistence tests all target SQLite.
    /// </summary>
    public const string SqliteProvider = "Sqlite";

    public const string DisabledProvider = "None";

    public const string DisabledProviderAlias = "Disabled";

    public string Provider { get; init; } = SqliteProvider;

    public string ConnectionStringName { get; init; } = "ApplicationDatabase";

    public DataDiagnosticsOptions Diagnostics { get; init; } = new();

    public DataAuditingOptions Auditing { get; init; } = new();

    public static bool IsDisabledProvider(string? provider)
    {
        string normalizedProvider = provider?.Trim() ?? string.Empty;

        return normalizedProvider.Equals(DisabledProvider, StringComparison.OrdinalIgnoreCase)
            || normalizedProvider.Equals(DisabledProviderAlias, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns whether the configured provider is one this reference application supports: SQLite, or data access disabled.
    /// </summary>
    public static bool IsSupportedProvider(string? provider)
    {
        string normalizedProvider = provider?.Trim() ?? string.Empty;

        return normalizedProvider.Equals(SqliteProvider, StringComparison.OrdinalIgnoreCase)
            || IsDisabledProvider(normalizedProvider);
    }
}
