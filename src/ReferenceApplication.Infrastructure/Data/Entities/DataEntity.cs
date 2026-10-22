namespace ReferenceApplication.Infrastructure.Data.Entities;

/// <summary>
/// Provides a common Guid primary key for ReferenceApplication data entities.
/// </summary>
public abstract class DataEntity
{
    /// <summary>
    /// Gets or sets the primary key.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();


    /// <summary>
    /// Gets or sets the application-managed optimistic concurrency token used to detect conflicting updates.
    /// </summary>
    /// <remarks>
    /// This token is application-managed so the model stays provider-neutral. SQLite is the only provider this reference
    /// application supports; an adopter that adds its own SQL Server provider and migration history can replace this
    /// pattern with a provider-native rowversion column when appropriate.
    /// </remarks>
    public string ConcurrencyStamp { get; set; } = NewConcurrencyStamp();

    internal static string NewConcurrencyStamp()
    {
        return Guid.NewGuid().ToString("N");
    }
}
