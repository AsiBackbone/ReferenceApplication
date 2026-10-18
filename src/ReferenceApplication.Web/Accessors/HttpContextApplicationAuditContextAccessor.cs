using System.Diagnostics;
using System.Security.Claims;
using ReferenceApplication.Infrastructure.Data.Auditing;
using ReferenceApplication.Web.Authentication.Claims;
using ReferenceApplication.Web.Governance;

namespace ReferenceApplication.Web.Accessors;

/// <summary>
/// Resolves structured application audit context from the active HTTP request.
/// </summary>
/// <param name="httpContextAccessor">The HTTP context accessor.</param>
/// <param name="governanceEvidenceAccessor">
/// The optional request-scoped governance evidence. When the request persisted a governance decision, mutation audit
/// records carry its decision audit record identifier and the decision's correlation and trace identifiers.
/// </param>
public sealed class HttpContextApplicationAuditContextAccessor(
    IHttpContextAccessor httpContextAccessor,
    IGovernanceDecisionEvidenceAccessor? governanceEvidenceAccessor = null)
    : IApplicationAuditContextAccessor
{
    private const string _subjectClaimType = "sub";

    public ApplicationAuditContext Current
    {
        get
        {
            HttpContext? httpContext = httpContextAccessor.HttpContext;
            ClaimsPrincipal? user = httpContext?.User;

            // Prefer the normalized application claim: claims transformation may remove the provider claims.
            string? subject = GetAuthenticatedClaim(user, ApplicationClaimTypes.Subject)
                ?? GetAuthenticatedClaim(user, _subjectClaimType)
                ?? GetAuthenticatedClaim(user, ClaimTypes.NameIdentifier);

            if (!string.IsNullOrWhiteSpace(subject))
            {
                GovernanceDecisionEvidence? evidence = governanceEvidenceAccessor?.Current;
                if (evidence is not null)
                {
                    // A governed request links its mutation audit to the persisted decision. The correlation and trace
                    // identifiers are taken from the decision record so the AsiBackbone audit-completion publisher can
                    // verify that the completion belongs to that decision.
                    return new ApplicationAuditContext(
                        subject,
                        ApplicationAuditActorTypes.Human,
                        subject,
                        operationExecutionId: evidence.OperationExecutionId,
                        correlationId: evidence.CorrelationId,
                        traceId: evidence.TraceId,
                        spanId: Activity.Current?.SpanId.ToString(),
                        decisionAuditRecordId: evidence.DecisionAuditRecordId);
                }

                return new ApplicationAuditContext(
                    subject,
                    ApplicationAuditActorTypes.Human,
                    subject,
                    correlationId: httpContext?.TraceIdentifier,
                    traceId: Activity.Current?.TraceId.ToString(),
                    spanId: Activity.Current?.SpanId.ToString());
            }

            string? remoteIp = httpContext?.Connection.RemoteIpAddress?.ToString();

            return !string.IsNullOrWhiteSpace(remoteIp)
                ? new ApplicationAuditContext(
                    remoteIp,
                    ApplicationAuditActorTypes.Network,
                    $"Remote IP: {remoteIp}",
                    correlationId: httpContext?.TraceIdentifier,
                    traceId: Activity.Current?.TraceId.ToString(),
                    spanId: Activity.Current?.SpanId.ToString())
                : new ApplicationAuditContext(
                "Unknown",
                ApplicationAuditActorTypes.Unknown,
                "Unknown",
                correlationId: httpContext?.TraceIdentifier,
                traceId: Activity.Current?.TraceId.ToString(),
                spanId: Activity.Current?.SpanId.ToString());
        }
    }

    private static string? GetAuthenticatedClaim(
        ClaimsPrincipal? user,
        string claimType)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        string? value = user.FindFirst(claimType)?.Value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
