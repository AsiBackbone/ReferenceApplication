using AsiBackbone.AspNetCore.Endpoints;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReferenceApplication.Web.Authentication.Extensions;
using ReferenceApplication.Web.Governance;

namespace ReferenceApplication.Web.Controllers.Api;

/// <summary>
/// Demonstrates host-owned governance enforcement on authenticated application operations.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Authorize(Policy = ApplicationAuthorizationPolicyNames.AuthenticatedUser)]
[Route("api/v{version:apiVersion}/governed-operations")]
public sealed class GovernedOperationsController : ControllerBase
{
    /// <summary>
    /// Describes the sample operations without performing a consequential action.
    /// </summary>
    /// <returns>A description of the endpoint-governance sample.</returns>
    /// <remarks>
    /// This read-only endpoint is intentionally anonymous and carries no AsiBackbone governance metadata.
    /// </remarks>
    [HttpGet]
    [AllowAnonymous]
    public ActionResult<GovernedOperationResponse> Get()
    {
        return Ok(new GovernedOperationResponse(
            Operation: "describe-governed-operations",
            Message: "Read-only description returned without a consequential operation."));
    }

    /// <summary>
    /// Executes a sample operation only for an actor in a configured administrator role.
    /// </summary>
    /// <returns>A response confirming that the governed action executed.</returns>
    [HttpPost("administrator")]
    [ValidateAntiForgeryToken]
    [GovernancePolicy(typeof(AdministratorActionPolicy))]
    public ActionResult<GovernedOperationResponse> ExecuteAdministratorAction()
    {
        return Ok(new GovernedOperationResponse(
            Operation: "administrator-action",
            Message: "Administrator-governed action executed."));
    }

    /// <summary>
    /// Executes a sample operation only when the actor holds the declared endpoint capability.
    /// </summary>
    /// <returns>A response confirming that the governed action executed.</returns>
    [HttpPost("capability")]
    [ValidateAntiForgeryToken]
    [GovernancePolicy(typeof(CapabilityProtectedActionPolicy))]
    [RequireCapabilityGrant(ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope)]
    public ActionResult<GovernedOperationResponse> ExecuteCapabilityAction()
    {
        return Ok(new GovernedOperationResponse(
            Operation: "capability-protected-action",
            Message: "Capability-protected action executed."));
    }

    /// <summary>
    /// Requests a sample consequential operation that requires administrator role, capability grant, and explicit
    /// acknowledgment before execution.
    /// </summary>
    /// <returns>An acknowledgment challenge; the action body does not run until a later host flow handles it.</returns>
    [HttpPost("consequential")]
    [ValidateAntiForgeryToken]
    [GovernancePolicy(typeof(AdministratorActionPolicy))]
    [GovernancePolicy(typeof(CapabilityProtectedActionPolicy))]
    [GovernancePolicy(typeof(ConsequentialActionPolicy))]
    [RequireCapabilityGrant(ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope)]
    [RequireAcknowledgment]
    public ActionResult<GovernedOperationResponse> ExecuteConsequentialAction()
    {
        return Ok(new GovernedOperationResponse(
            Operation: "consequential-action",
            Message: "Consequential action executed."));
    }
}

/// <summary>
/// Represents the result returned after a sample governed operation executes.
/// </summary>
/// <param name="Operation">The stable operation name.</param>
/// <param name="Message">A short execution result.</param>
public sealed record GovernedOperationResponse(string Operation, string Message);
