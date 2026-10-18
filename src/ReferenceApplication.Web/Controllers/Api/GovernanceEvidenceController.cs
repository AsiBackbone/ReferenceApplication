using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReferenceApplication.Web.Authentication.Extensions;
using ReferenceApplication.Web.Governance;

namespace ReferenceApplication.Web.Controllers.Api;

/// <summary>
/// Exposes minimized governance evidence for operational inspection.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Authorize(Policy = ApplicationAuthorizationPolicyNames.AdministratorRole)]
[Route("api/v{version:apiVersion}/governance-evidence")]
public sealed class GovernanceEvidenceController(IGovernanceEvidenceQuery evidenceQuery) : ControllerBase
{
    private readonly IGovernanceEvidenceQuery _evidenceQuery =
        evidenceQuery ?? throw new ArgumentNullException(nameof(evidenceQuery));

    /// <summary>
    /// Gets the ordered, minimized evidence timeline for a governed request.
    /// </summary>
    /// <param name="correlationId">The exact correlation identifier recorded for the request.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The evidence timeline, or not found when the correlation identifier is unknown.</returns>
    [HttpGet("{correlationId}")]
    [ProducesResponseType<GovernanceEvidenceTimelineResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GovernanceEvidenceTimelineResponse>> Get(
        [FromRoute, Required, StringLength(128, MinimumLength = 1)] string correlationId,
        CancellationToken cancellationToken)
    {
        GovernanceEvidenceTimelineResponse? evidence = await _evidenceQuery
            .FindAsync(correlationId, cancellationToken)
            .ConfigureAwait(false);

        return evidence is null ? NotFound() : Ok(evidence);
    }
}
