using System.ComponentModel.DataAnnotations;
using System.Text.Json;
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
public sealed class GovernanceEvidenceController(
    IGovernanceEvidenceQuery evidenceQuery,
    IGovernanceEvidenceVerifier evidenceVerifier,
    IGovernanceEvidenceNotifier evidenceNotifier,
    TimeProvider timeProvider) : ControllerBase
{
    /// <summary>
    /// The interval between SSE comment heartbeats on an otherwise idle notification stream.
    /// </summary>
    /// <remarks>
    /// Proxies and load balancers commonly close responses that send nothing for 60 seconds or more. A comment line
    /// keeps the connection observably alive without producing an event for the client.
    /// </remarks>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IGovernanceEvidenceQuery _evidenceQuery =
        evidenceQuery ?? throw new ArgumentNullException(nameof(evidenceQuery));
    private readonly IGovernanceEvidenceNotifier _evidenceNotifier =
        evidenceNotifier ?? throw new ArgumentNullException(nameof(evidenceNotifier));
    private readonly IGovernanceEvidenceVerifier _evidenceVerifier =
        evidenceVerifier ?? throw new ArgumentNullException(nameof(evidenceVerifier));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>
    /// Streams identifier-only hints whenever persisted governance evidence changes.
    /// </summary>
    /// <param name="correlationId">An optional exact correlation identifier to filter. Omit it to observe all evidence.</param>
    /// <param name="cancellationToken">The request-aborted token that ends the subscription.</param>
    /// <remarks>
    /// This administrator-only endpoint deliberately sends no evidence payload. Clients re-query the evidence API by
    /// correlation identifier so the displayed timeline always comes from persisted state. A <c>: keep-alive</c>
    /// comment is written every <see cref="HeartbeatInterval" /> so intermediaries do not close an idle stream.
    /// </remarks>
    [HttpGet("notifications")]
    [Produces("text/event-stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task StreamNotifications(
        [FromQuery, StringLength(128, MinimumLength = 1)] string? correlationId,
        CancellationToken cancellationToken)
    {
        string? normalizedCorrelationId = string.IsNullOrWhiteSpace(correlationId)
            ? null
            : correlationId.Trim();

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache, no-store";
        Response.Headers.Append("X-Accel-Buffering", "no");
        await using IGovernanceEvidenceSubscription subscription = _evidenceNotifier.Subscribe();
        await Response.StartAsync(cancellationToken).ConfigureAwait(false);
        await Response.WriteAsync(": connected\n\n", cancellationToken).ConfigureAwait(false);
        await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

        // Events and heartbeats are written from two loops; the lock keeps each SSE frame contiguous.
        using var writeLock = new SemaphoreSlim(1, 1);
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task heartbeats = WriteHeartbeatsAsync(writeLock, heartbeatCancellation.Token);

        try
        {
            await foreach (GovernanceEvidenceNotification notification in subscription
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                if (normalizedCorrelationId is not null &&
                    !string.Equals(notification.CorrelationId, normalizedCorrelationId, StringComparison.Ordinal))
                {
                    continue;
                }

                await WriteFrameAsync(
                    writeLock,
                    $"event: evidence\ndata: {JsonSerializer.Serialize(notification, _jsonOptions)}\n\n",
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client disconnects are the normal way an SSE subscription ends.
        }
        finally
        {
            await heartbeatCancellation.CancelAsync().ConfigureAwait(false);
            await heartbeats.ConfigureAwait(false);
        }
    }

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

    /// <summary>
    /// Recomputes the canonical hash of each decision ledger record for a governed request.
    /// </summary>
    /// <param name="correlationId">The exact correlation identifier recorded for the request.</param>
    /// <param name="cancellationToken">A token that cancels the verification.</param>
    /// <returns>The record-level verification results, or not found when the correlation identifier is unknown.</returns>
    [HttpGet("{correlationId}/verification")]
    [ProducesResponseType<GovernanceEvidenceVerificationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GovernanceEvidenceVerificationResponse>> Verify(
        [FromRoute, Required, StringLength(128, MinimumLength = 1)] string correlationId,
        CancellationToken cancellationToken)
    {
        GovernanceEvidenceVerificationResponse? verification = await _evidenceVerifier
            .VerifyAsync(correlationId, cancellationToken)
            .ConfigureAwait(false);

        return verification is null ? NotFound() : Ok(verification);
    }

    private async Task WriteHeartbeatsAsync(SemaphoreSlim writeLock, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval, _timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await WriteFrameAsync(writeLock, ": keep-alive\n\n", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The stream ended or the client disconnected.
        }
    }

    private async Task WriteFrameAsync(SemaphoreSlim writeLock, string frame, CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await Response.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = writeLock.Release();
        }
    }
}
