namespace ReferenceApplication.Web.Governance.Demonstration;

/// <summary>
/// Configures the bounded, Development-only governance load demonstration.
/// </summary>
public sealed class GovernanceLoadDemonstrationOptions
{
    /// <summary>
    /// Gets the configuration section for the demonstration.
    /// </summary>
    public const string SectionName = "ReferenceApplication:GovernanceLoadDemonstration";

    /// <summary>
    /// Gets the absolute iteration ceiling accepted from configuration.
    /// </summary>
    public const int AbsoluteMaximumIterations = 10_000;

    /// <summary>
    /// Gets the absolute duration ceiling accepted from configuration.
    /// </summary>
    public const int AbsoluteMaximumDurationMilliseconds = 30_000;

    /// <summary>
    /// Gets or sets a value indicating whether the demonstration is explicitly enabled.
    /// The feature remains unavailable outside Development.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the default number of decisions in a run.
    /// </summary>
    public int DefaultIterations { get; set; } = 2_000;

    /// <summary>
    /// Gets or sets the maximum number of decisions allowed in a run.
    /// </summary>
    public int MaximumIterations { get; set; } = 5_000;

    /// <summary>
    /// Gets or sets the default duration limit in milliseconds.
    /// </summary>
    public int DefaultDurationMilliseconds { get; set; } = 5_000;

    /// <summary>
    /// Gets or sets the maximum duration allowed in milliseconds.
    /// </summary>
    public int MaximumDurationMilliseconds { get; set; } = 10_000;

    /// <summary>
    /// Gets or sets the minimum interval between progress notifications in milliseconds.
    /// </summary>
    public int ProgressIntervalMilliseconds { get; set; } = 100;
}
