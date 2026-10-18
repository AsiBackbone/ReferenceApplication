using AsiBackbone.AspNetCore.Actors;
using AsiBackbone.AspNetCore.Endpoints;
using AsiBackbone.Core.Actors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReferenceApplication.Web.Authentication.Claims;
using ReferenceApplication.Web.Governance;

namespace ReferenceApplication.Web.Tests.TestControllers;

/// <summary>
/// Provides test endpoints for verifying the AsiBackbone service registration and endpoint-governance middleware
/// added in walkthrough Steps 3 and 4.
/// </summary>
[ApiController]
[Route("test/asibackbone")]
public sealed class AsiBackboneTestController : ControllerBase
{
    /// <summary>
    /// The response body returned when an action executes.
    /// </summary>
    public const string ExecutedMarker = "action-executed";

    /// <summary>
    /// Returns the governance actor that AsiBackbone resolves for the current request, alongside the normalized
    /// application subject claim produced by the NCAT claims transformation.
    /// </summary>
    /// <param name="resolver">The AsiBackbone HTTP actor resolver.</param>
    /// <returns>The resolved actor values.</returns>
    [HttpGet("actor")]
    [AllowAnonymous]
    public IActionResult Actor([FromServices] IHttpGovernanceActorContextResolver resolver)
    {
        IGovernanceActorContext actor = resolver.ResolveActorContext();

        return Ok(new
        {
            actor.ActorId,
            actor.DisplayName,
            ActorType = actor.ActorType.ToString(),
            actor.IsAuthenticated,
            actor.IsKnown,
            ApplicationSubject = User.FindFirst(ApplicationClaimTypes.Subject)?.Value
        });
    }

    /// <summary>
    /// An endpoint without AsiBackbone governance metadata, which the middleware must pass through unchanged.
    /// </summary>
    /// <returns>A response confirming the action executed.</returns>
    [HttpGet("ungoverned")]
    [AllowAnonymous]
    public IActionResult Ungoverned()
    {
        return Ok(ExecutedMarker);
    }

    /// <summary>
    /// An endpoint carrying an unrelated test policy marker. The Step 4 evaluator runs, finds no applicable sample
    /// rule, and allows the request.
    /// </summary>
    /// <returns>A response confirming the action executed.</returns>
    [HttpGet("governed")]
    [AllowAnonymous]
    [GovernancePolicy(typeof(TestGovernancePolicy))]
    public IActionResult Governed()
    {
        return Ok(ExecutedMarker);
    }

    /// <summary>
    /// An endpoint governed by the Step 4 administrator rule.
    /// </summary>
    /// <returns>A response confirming the action executed, which only an administrator may receive.</returns>
    [HttpPost("administrator-action")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [GovernancePolicy(typeof(AdministratorActionPolicy))]
    public IActionResult AdministratorAction()
    {
        return Ok(ExecutedMarker);
    }

    /// <summary>
    /// An endpoint governed by the Step 4 consequential-action rule, which requires acknowledgment before execution.
    /// </summary>
    /// <returns>A response confirming the action executed, which an unacknowledged request must never receive.</returns>
    [HttpPost("consequential-action")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [GovernancePolicy(typeof(ConsequentialActionPolicy))]
    public IActionResult ConsequentialAction()
    {
        return Ok(ExecutedMarker);
    }

    /// <summary>
    /// An endpoint that carries the Step 4 capability-protected marker without declaring the required capability
    /// scope, which the capability-declaration rule must reject.
    /// </summary>
    /// <returns>A response confirming the action executed, which this incomplete declaration must never receive.</returns>
    [HttpPost("capability-action-without-scope")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [GovernancePolicy(typeof(CapabilityProtectedActionPolicy))]
    public IActionResult CapabilityActionWithoutScope()
    {
        return Ok(ExecutedMarker);
    }

    /// <summary>
    /// A host-defined policy marker type for the governed test endpoint.
    /// </summary>
    private sealed class TestGovernancePolicy;
}
