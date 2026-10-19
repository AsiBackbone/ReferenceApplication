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
    /// Gets or sets the display name of the current actor.
    /// </summary>
    public string ActorDisplayName { get; set; } = "Authenticated actor";
}
