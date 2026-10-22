using System.ComponentModel.DataAnnotations;
using AsiBackbone.AspNetCore.Endpoints;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReferenceApplication.Infrastructure.Data.Auditing;
using ReferenceApplication.Infrastructure.Data.Configurations;
using ReferenceApplication.Infrastructure.Data.Entities;
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
    [EmitGovernanceAudit]
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
    [EmitGovernanceAudit]
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
    [EmitGovernanceAudit]
    [GovernancePolicy(typeof(AdministratorActionPolicy))]
    [GovernancePolicy(typeof(CapabilityProtectedActionPolicy))]
    [GovernancePolicy(typeof(ConsequentialActionPolicy))]
    [RequireCapabilityGrant(ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope)]
    [RequireAcknowledgment]
    public async Task<ActionResult<GovernedOperationResponse>> ExecuteConsequentialAction(
        [FromServices] ApplicationAcknowledgmentWorkflow acknowledgmentWorkflow,
        CancellationToken cancellationToken)
    {
        IApplicationAcknowledgmentFeature feature = HttpContext.Features.Get<IApplicationAcknowledgmentFeature>()
            ?? throw new InvalidOperationException(
                "The consequential action reached execution without a validated acknowledgment.");

        ApplicationAcknowledgmentConsumptionResult consumption = await acknowledgmentWorkflow
            .ConsumeAsync(feature, cancellationToken)
            .ConfigureAwait(false);
        return !consumption.Succeeded
            ? (ActionResult<GovernedOperationResponse>)Problem(
                title: "Acknowledgment denied.",
                detail: consumption.Message,
                statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?>
                {
                    ["reasonCodes"] = new[] { consumption.ReasonCode }
                })
            : (ActionResult<GovernedOperationResponse>)Ok(new GovernedOperationResponse(
            Operation: "consequential-action",
            Message: "Consequential action executed after acknowledgment."));
    }

    /// <summary>
    /// Creates a sample application record as an administrator-governed, audited mutation (walkthrough Step 6).
    /// </summary>
    /// <param name="request">The record to create.</param>
    /// <param name="auditedTransaction">The NCAT audited transaction coordinator.</param>
    /// <param name="completionOutbox">The NCAT audit-completion outbox.</param>
    /// <param name="evidenceAccessor">The governance decision evidence persisted for this request.</param>
    /// <param name="notificationPublisher">The isolated best-effort evidence notification publisher.</param>
    /// <param name="cancellationToken">A token that cancels the request.</param>
    /// <returns>The created record and the identifiers that link it to its governance decision.</returns>
    /// <remarks>
    /// Endpoint governance persists the decision receipt to the AsiBackbone audit ledger before this action runs. The
    /// mutation then runs inside NCAT's audited transaction, so its mutation-audit records carry the ledger record
    /// identifier, and the same transaction stages an audit-completion outbox entry. The outbox later delivers that
    /// entry to <see cref="AsiBackboneAuditCompletionPublisher" />, which appends the matching AsiBackbone lifecycle
    /// event.
    /// </remarks>
    [HttpPost("records")]
    [ValidateAntiForgeryToken]
    [EmitGovernanceAudit]
    [GovernancePolicy(typeof(AdministratorActionPolicy))]
    [ProducesResponseType<GovernedRecordCreatedResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<GovernedRecordCreatedResponse>> CreateRecord(
        [FromForm] CreateGovernedRecordRequest request,
        [FromServices] IApplicationAuditedTransaction auditedTransaction,
        [FromServices] IApplicationAuditCompletionOutbox completionOutbox,
        [FromServices] IGovernanceDecisionEvidenceAccessor evidenceAccessor,
        [FromServices] GovernanceEvidenceNotificationPublisher notificationPublisher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(auditedTransaction);
        ArgumentNullException.ThrowIfNull(completionOutbox);
        ArgumentNullException.ThrowIfNull(evidenceAccessor);
        ArgumentNullException.ThrowIfNull(notificationPublisher);

        // Endpoint governance persisted the decision before this action ran; without it the mutation would be
        // unlinked, so fail rather than write an unattributable change.
        GovernanceDecisionEvidence evidence = evidenceAccessor.Current
            ?? throw new InvalidOperationException("No governance decision evidence was persisted for this request.");

        var record = new GovernedOperationRecord
        {
            OperationName = evidence.OperationName,
            Title = request.Title.Trim(),
            CreatedUtc = DateTimeOffset.UtcNow
        };

        ApplicationAuditedTransactionResult result = await auditedTransaction.ExecuteAsync(
            (dbContext, mutationToken) =>
            {
                mutationToken.ThrowIfCancellationRequested();
                _ = dbContext.GovernedOperationRecords.Add(record);
                return Task.CompletedTask;
            },
            async (dbContext, receipt, token) =>
                _ = await completionOutbox.StageAsync(dbContext, receipt, cancellationToken: token).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.AuditReceipt is { } auditReceipt && !string.IsNullOrWhiteSpace(auditReceipt.CorrelationId))
        {
            notificationPublisher.Publish(new GovernanceEvidenceNotification(
                auditReceipt.CorrelationId,
                GovernanceEvidenceNotificationKinds.MutationAudit,
                auditReceipt.MutationBatchId));
        }

        return StatusCode(StatusCodes.Status201Created, new GovernedRecordCreatedResponse(
            RecordId: record.Id,
            Title: record.Title,
            DecisionAuditRecordId: evidence.DecisionAuditRecordId,
            OperationExecutionId: evidence.OperationExecutionId,
            MutationBatchId: result.AuditReceipt?.MutationBatchId));
    }
}

/// <summary>
/// Represents the result returned after a sample governed operation executes.
/// </summary>
/// <param name="Operation">The stable operation name.</param>
/// <param name="Message">A short execution result.</param>
public sealed record GovernedOperationResponse(string Operation, string Message);

/// <summary>
/// Represents a request to create a sample governed record.
/// </summary>
public sealed class CreateGovernedRecordRequest
{
    /// <summary>
    /// Gets or sets the short title of the record.
    /// </summary>
    [Required]
    [StringLength(GovernedOperationRecordConfiguration.TitleMaxLength, MinimumLength = 1)]
    public string Title { get; set; } = string.Empty;
}

/// <summary>
/// Represents the result of creating a sample governed record.
/// </summary>
/// <param name="RecordId">The created record identifier.</param>
/// <param name="Title">The stored record title.</param>
/// <param name="DecisionAuditRecordId">The AsiBackbone audit ledger record of the decision that permitted the change.</param>
/// <param name="OperationExecutionId">The governed execution identifier shared by NCAT and AsiBackbone evidence.</param>
/// <param name="MutationBatchId">The NCAT mutation-audit batch identifier.</param>
public sealed record GovernedRecordCreatedResponse(
    Guid RecordId,
    string Title,
    string DecisionAuditRecordId,
    string OperationExecutionId,
    string? MutationBatchId);
