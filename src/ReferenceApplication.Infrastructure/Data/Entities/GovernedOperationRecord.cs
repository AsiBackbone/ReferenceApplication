namespace ReferenceApplication.Infrastructure.Data.Entities;

/// <summary>
/// Represents a sample application record created by a governed operation in walkthrough Step 6.
/// </summary>
/// <remarks>
/// The record exists to give the governed operation a real, NCAT-audited mutation. Its mutation-audit records carry the
/// AsiBackbone decision audit record identifier, which links the change to the governance decision that permitted it.
/// </remarks>
public sealed class GovernedOperationRecord : DataEntity
{
    /// <summary>
    /// Gets or sets the stable name of the governed operation that created the record.
    /// </summary>
    public string OperationName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the short caller-supplied title of the record.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the record was created, in UTC.
    /// </summary>
    public DateTimeOffset CreatedUtc { get; set; }
}
