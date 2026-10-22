using System.ComponentModel.DataAnnotations;
using AsiBackbone.AspNetCore.Acknowledgments;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReferenceApplication.Web.Authentication.Extensions;
using ReferenceApplication.Web.Governance;

namespace ReferenceApplication.Web.Controllers.Api;

/// <summary>
/// Completes host-owned acknowledgment challenges before a consequential retry.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Authorize(Policy = ApplicationAuthorizationPolicyNames.AuthenticatedUser)]
[Route("api/v{version:apiVersion}/acknowledgments")]
public sealed class AcknowledgmentsController(ApplicationAcknowledgmentWorkflow workflow) : ControllerBase
{
    private readonly ApplicationAcknowledgmentWorkflow _workflow =
        workflow ?? throw new ArgumentNullException(nameof(workflow));

    /// <summary>
    /// Records the current actor's response to a persisted acknowledgment challenge.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [ProducesResponseType<AcknowledgmentSubmissionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AcknowledgmentSubmissionResponse>> Submit(
        [FromForm] SubmitAcknowledgmentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        ApplicationAcknowledgmentSubmissionResult result = await _workflow
            .SubmitAsync(
                new AcknowledgmentChallengeRequest
                {
                    HandshakeId = request.HandshakeId,
                    AcknowledgmentCode = request.AcknowledgmentCode,
                    Acknowledged = request.Acknowledged
                },
                cancellationToken)
            .ConfigureAwait(false);

        return !result.Succeeded || result.Feature is null
            ? (ActionResult<AcknowledgmentSubmissionResponse>)Problem(
                title: "Acknowledgment denied.",
                detail: result.Message,
                statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?>
                {
                    ["reasonCodes"] = new[] { result.ReasonCode! }
                })
            : (ActionResult<AcknowledgmentSubmissionResponse>)Ok(new AcknowledgmentSubmissionResponse(
            result.Feature.HandshakeId,
            result.Feature.AcknowledgmentId,
            result.HeaderName!,
            result.Feature.ExpiresUtc));
    }
}

/// <summary>
/// The actor's response to an acknowledgment challenge.
/// </summary>
public sealed class SubmitAcknowledgmentRequest
{
    [Required]
    [StringLength(128, MinimumLength = 1)]
    public string HandshakeId { get; set; } = string.Empty;

    [Required]
    [StringLength(128, MinimumLength = 1)]
    public string AcknowledgmentCode { get; set; } = string.Empty;

    public bool Acknowledged { get; set; }
}

/// <summary>
/// The accepted acknowledgment and the header used for the consequential retry.
/// </summary>
public sealed record AcknowledgmentSubmissionResponse(
    string HandshakeId,
    string AcknowledgmentId,
    string HeaderName,
    DateTimeOffset ExpiresUtc);
