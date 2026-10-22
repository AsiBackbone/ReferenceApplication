namespace ReferenceApplication.Web.Governance.FailureScenarios;

/// <summary>
/// Configures the Development-only, administrator-only failure scenarios in the governance explorer.
/// </summary>
public sealed class FailureScenarioOptions
{
    /// <summary>
    /// Gets the configuration section for the failure scenarios.
    /// </summary>
    public const string SectionName = "ReferenceApplication:FailureScenarios";

    /// <summary>
    /// Gets the request header that names the armed run a governed request belongs to.
    /// </summary>
    public const string RunHeaderName = "X-Failure-Scenario-Run";

    /// <summary>
    /// Gets the longest run identifier accepted.
    /// </summary>
    public const int MaximumRunIdLength = 64;

    /// <summary>
    /// Gets the number of runs kept; the oldest is forgotten, with any delivery fault it holds, when another is armed.
    /// </summary>
    public const int RetainedRunCount = 32;

    /// <summary>
    /// Gets how long an armed run waits for its request before it expires unused.
    /// </summary>
    public static readonly TimeSpan ArmLifetime = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets or sets a value indicating whether the failure scenarios are explicitly enabled. They remain unavailable
    /// outside Development, and also while the disposable demonstration database is not in use.
    /// </summary>
    public bool Enabled { get; set; }
}
