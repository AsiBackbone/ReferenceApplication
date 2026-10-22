namespace ReferenceApplication.Web.Governance.Pipeline;

/// <summary>
/// Configures the Development-only, administrator-only pipeline inspector in the governance explorer.
/// </summary>
public sealed class PipelineInspectorOptions
{
    /// <summary>
    /// Gets the configuration section for the pipeline inspector.
    /// </summary>
    public const string SectionName = "ReferenceApplication:PipelineInspector";

    /// <summary>
    /// Gets the number of outbox entries and open findings each snapshot returns.
    /// </summary>
    public const int ListSize = 25;

    /// <summary>
    /// Gets the number of spans, and separately of metric measurements, the in-process listener retains.
    /// </summary>
    public const int TelemetryCapacity = 512;

    /// <summary>
    /// Gets the longest correlation identifier accepted.
    /// </summary>
    public const int MaximumCorrelationIdLength = 128;

    /// <summary>
    /// Gets or sets a value indicating whether the pipeline inspector is explicitly enabled.
    /// The inspector remains unavailable outside Development.
    /// </summary>
    public bool Enabled { get; set; }
}
