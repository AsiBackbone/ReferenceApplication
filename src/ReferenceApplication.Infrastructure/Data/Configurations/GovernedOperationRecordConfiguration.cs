using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReferenceApplication.Infrastructure.Data.Entities;

namespace ReferenceApplication.Infrastructure.Data.Configurations;

/// <summary>
/// Configures the EF Core mapping for <see cref="GovernedOperationRecord" />.
/// </summary>
public sealed class GovernedOperationRecordConfiguration : IEntityTypeConfiguration<GovernedOperationRecord>
{
    /// <summary>
    /// The maximum length of <see cref="GovernedOperationRecord.OperationName" />.
    /// </summary>
    public const int OperationNameMaxLength = 200;

    /// <summary>
    /// The maximum length of <see cref="GovernedOperationRecord.Title" />.
    /// </summary>
    public const int TitleMaxLength = 200;

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<GovernedOperationRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("GovernedOperationRecords");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .ValueGeneratedNever();

        builder.Property(x => x.OperationName)
            .HasMaxLength(OperationNameMaxLength)
            .IsRequired();

        builder.Property(x => x.Title)
            .HasMaxLength(TitleMaxLength)
            .IsRequired();

        builder.Property(x => x.CreatedUtc)
            .IsRequired();

        builder.HasIndex(x => x.CreatedUtc);
    }
}
