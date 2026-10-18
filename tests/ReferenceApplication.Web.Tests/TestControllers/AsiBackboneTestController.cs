using AsiBackbone.AspNetCore.Actors;
using AsiBackbone.AspNetCore.Endpoints;
using AsiBackbone.Core.Actors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReferenceApplication.Web.Authentication.Claims;

namespace ReferenceApplication.Web.Tests.TestControllers;

/// <summary>
/// Provides test endpoints for verifying the AsiBackbone service registration and endpoint-governance middleware
/// added in walkthrough Step 3.
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
    /// An endpoint carrying AsiBackbone governance metadata. Step 3 registers no policy evaluator, so the middleware
    /// must stop the request before this action executes.
    /// </summary>
    /// <returns>A response confirming the action executed, which a governed request must never receive.</returns>
    [HttpGet("governed")]
    [AllowAnonymous]
    [GovernancePolicy(typeof(TestGovernancePolicy))]
    public IActionResult Governed()
    {
        return Ok(ExecutedMarker);
    }

    /// <summary>
    /// A host-defined policy marker type for the governed test endpoint.
    /// </summary>
    private sealed class TestGovernancePolicy;
}
