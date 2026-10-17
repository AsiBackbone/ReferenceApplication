using ReferenceApplication.Infrastructure.Data.Auditing;

namespace ReferenceApplication.Infrastructure.Data.Options;

public sealed class DataAuditingOptions
{
    public bool Enabled { get; init; } = true;

    public string StorageMode { get; init; } = AuditStorageModes.Local;
}
