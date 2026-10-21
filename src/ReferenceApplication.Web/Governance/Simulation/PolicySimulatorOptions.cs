namespace ReferenceApplication.Web.Governance.Simulation;

/// <summary>
/// Configures the Development-only, administrator-only policy simulator in the governance explorer.
/// </summary>
public sealed class PolicySimulatorOptions
{
    /// <summary>
    /// Gets the configuration section for the simulator.
    /// </summary>
    public const string SectionName = "ReferenceApplication:PolicySimulator";

    /// <summary>
    /// Gets the longest sample payload accepted, matching the sample record title limit.
    /// </summary>
    public const int MaximumPayloadLength = 160;

    /// <summary>
    /// Gets or sets a value indicating whether the simulator is explicitly enabled.
    /// The simulator remains unavailable outside Development.
    /// </summary>
    public bool Enabled { get; set; }
}
