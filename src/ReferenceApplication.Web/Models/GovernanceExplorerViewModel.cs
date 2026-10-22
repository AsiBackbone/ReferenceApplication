using ReferenceApplication.Web.Governance.FailureScenarios;

namespace ReferenceApplication.Web.Models;

/// <summary>
/// Describes the authenticated actor and Development-only controls available to the governance explorer.
/// </summary>
public sealed class GovernanceExplorerViewModel
{
    /// <summary>
    /// Gets or sets a value indicating whether the authenticated actor may read full governance evidence.
    /// </summary>
    public bool CanViewEvidence { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the Development-only demo identity control is active.
    /// </summary>
    public bool ShowDemoIdentityControls { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the current actor has an administrator role.
    /// </summary>
    public bool HasAdministratorRole { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the current actor has the consequential-operation capability.
    /// </summary>
    public bool HasConsequentialCapability { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the disposable Development database was just reset.
    /// </summary>
    public bool DemonstrationDatabaseWasReset { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the administrator may use the Development-only load demonstration.
    /// </summary>
    public bool ShowGovernanceLoadDemonstration { get; set; }

    /// <summary>
    /// Gets or sets the default decision count for the bounded load demonstration.
    /// </summary>
    public int LoadDemonstrationDefaultIterations { get; set; }

    /// <summary>
    /// Gets or sets the maximum decision count accepted by the bounded load demonstration.
    /// </summary>
    public int LoadDemonstrationMaximumIterations { get; set; }

    /// <summary>
    /// Gets or sets the default duration bound in milliseconds.
    /// </summary>
    public int LoadDemonstrationDefaultDurationMilliseconds { get; set; }

    /// <summary>
    /// Gets or sets the maximum duration bound in milliseconds.
    /// </summary>
    public int LoadDemonstrationMaximumDurationMilliseconds { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the administrator may use the Development-only policy simulator.
    /// </summary>
    public bool ShowPolicySimulator { get; set; }

    /// <summary>
    /// Gets or sets the registered constraint names the policy simulator can leave out, in evaluation order.
    /// </summary>
    public IReadOnlyList<string> PolicySimulatorConstraintNames { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the administrator may use the Development-only decision replay.
    /// </summary>
    public bool ShowDecisionReplay { get; set; }

    /// <summary>
    /// Gets or sets the registered constraint names a decision replay can leave out, in evaluation order.
    /// </summary>
    public IReadOnlyList<string> DecisionReplayConstraintNames { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the administrator may use the Development-only pipeline inspector.
    /// </summary>
    public bool ShowPipelineInspector { get; set; }

    /// <summary>
    /// Gets or sets the outbox statuses the pipeline inspector can filter entries by.
    /// </summary>
    public IReadOnlyList<string> PipelineOutboxStatuses { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the administrator may use the Development-only failure scenarios.
    /// </summary>
    public bool ShowFailureScenarios { get; set; }

    /// <summary>
    /// Gets or sets the failure scenarios, with whether the current actor can run each one.
    /// </summary>
    public IReadOnlyList<FailureScenarioView> FailureScenarios { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether signed capability tokens are enabled.
    /// </summary>
    public bool CapabilityTokensEnabled { get; set; }

    /// <summary>
    /// Gets or sets the display name of the current actor.
    /// </summary>
    public string ActorDisplayName { get; set; } = "Authenticated actor";
}
