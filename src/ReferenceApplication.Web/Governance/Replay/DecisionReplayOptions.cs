namespace ReferenceApplication.Web.Governance.Replay;

/// <summary>
/// Configures the Development-only, administrator-only decision replay in the governance explorer.
/// </summary>
public sealed class DecisionReplayOptions
{
    /// <summary>
    /// Gets the configuration section for decision replay.
    /// </summary>
    public const string SectionName = "ReferenceApplication:DecisionReplay";

    /// <summary>
    /// Gets the number of decisions on each page of the recent-decisions list.
    /// </summary>
    public const int PageSize = 25;

    /// <summary>
    /// Gets the longest decision receipt identifier accepted.
    /// </summary>
    public const int MaximumReceiptIdLength = 128;

    /// <summary>
    /// Gets or sets a value indicating whether decision replay is explicitly enabled.
    /// Decision replay remains unavailable outside Development.
    /// </summary>
    public bool Enabled { get; set; }
}
