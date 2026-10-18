using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using ReferenceApplication.Infrastructure.Data.Auditing;
using ReferenceApplication.Web.Accessors;
using ReferenceApplication.Web.Authentication.Claims;
using ReferenceApplication.Web.Governance;

namespace ReferenceApplication.Web.Tests;

public sealed class HttpContextApplicationAuditContextAccessorTests
{
    [Fact]
    public void Current_AuthenticatedUserWithOnlyNormalizedSubjectClaim_UsesSubject()
    {
        ApplicationAuditContext context = CreateAccessor(
            CreateAuthenticatedPrincipal(new Claim(ApplicationClaimTypes.Subject, "user-789"))).Current;

        Assert.Equal("user-789", context.ActorId);
        Assert.Equal(ApplicationAuditActorTypes.Human, context.ActorType);
    }

    [Fact]
    public void Current_AuthenticatedUserWithNormalizedAndProviderSubjectClaims_PrefersNormalizedSubject()
    {
        ApplicationAuditContext context = CreateAccessor(
            CreateAuthenticatedPrincipal(
                new Claim("sub", "provider-subject"),
                new Claim(ApplicationClaimTypes.Subject, "user-789"))).Current;

        Assert.Equal("user-789", context.ActorId);
        Assert.Equal(ApplicationAuditActorTypes.Human, context.ActorType);
    }

    [Fact]
    public void Current_AuthenticatedUserWithOnlyProviderSubjectClaim_UsesProviderSubject()
    {
        ApplicationAuditContext context = CreateAccessor(
            CreateAuthenticatedPrincipal(new Claim("sub", "provider-subject"))).Current;

        Assert.Equal("provider-subject", context.ActorId);
    }

    [Fact]
    public void Current_AuthenticatedUserWithoutSubjectClaims_UsesRemoteIpAddress()
    {
        ApplicationAuditContext context = CreateAccessor(CreateAuthenticatedPrincipal()).Current;

        Assert.Equal("192.0.2.10", context.ActorId);
        Assert.Equal(ApplicationAuditActorTypes.Network, context.ActorType);
    }

    [Fact]
    public void Current_GovernedRequest_LinksMutationAuditToPersistedDecision()
    {
        var evidence = new GovernanceDecisionEvidence(
            DecisionAuditRecordId: "ledger-record-1",
            DecisionReceiptId: "receipt-1",
            OperationName: "governed.operation",
            Outcome: "Allowed",
            OperationExecutionId: "execution-1",
            CorrelationId: "decision-correlation",
            TraceId: "0af7651916cd43dd8448eb211c80319c");

        ApplicationAuditContext context = CreateAccessor(
            CreateAuthenticatedPrincipal(new Claim(ApplicationClaimTypes.Subject, "user-789")),
            evidence).Current;

        Assert.Equal("user-789", context.ActorId);
        Assert.Equal(ApplicationAuditActorTypes.Human, context.ActorType);
        Assert.Equal("ledger-record-1", context.DecisionAuditRecordId);
        Assert.Equal("execution-1", context.OperationExecutionId);
        Assert.Equal("decision-correlation", context.CorrelationId);
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", context.TraceId);
    }

    [Fact]
    public void Current_GovernedRequestWithoutDecisionCorrelation_DoesNotSubstituteRequestIdentifiers()
    {
        // The audit-completion publisher rejects a completion whose correlation identifier differs from the decision's,
        // so a missing decision value must stay missing rather than fall back to the request trace identifier.
        var evidence = new GovernanceDecisionEvidence("ledger-record-1", null, "governed.operation", "Allowed", "execution-1", null, null);

        ApplicationAuditContext context = CreateAccessor(
            CreateAuthenticatedPrincipal(new Claim(ApplicationClaimTypes.Subject, "user-789")),
            evidence).Current;

        Assert.Null(context.CorrelationId);
        Assert.Null(context.TraceId);
        Assert.Equal("ledger-record-1", context.DecisionAuditRecordId);
    }

    [Fact]
    public void Current_UngovernedRequest_KeepsRequestCorrelationAndNoDecisionLink()
    {
        ApplicationAuditContext context = CreateAccessor(
            CreateAuthenticatedPrincipal(new Claim(ApplicationClaimTypes.Subject, "user-789")),
            evidence: null).Current;

        Assert.Equal("request-trace-identifier", context.CorrelationId);
        Assert.Null(context.DecisionAuditRecordId);
        Assert.Null(context.OperationExecutionId);
    }

    private static HttpContextApplicationAuditContextAccessor CreateAccessor(ClaimsPrincipal user)
    {
        var httpContext = new DefaultHttpContext { User = user };
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");

        return new HttpContextApplicationAuditContextAccessor(
            new HttpContextAccessor { HttpContext = httpContext });
    }

    private static HttpContextApplicationAuditContextAccessor CreateAccessor(
        ClaimsPrincipal user,
        GovernanceDecisionEvidence? evidence)
    {
        var httpContext = new DefaultHttpContext { User = user, TraceIdentifier = "request-trace-identifier" };
        var evidenceAccessor = new GovernanceDecisionEvidenceAccessor();
        if (evidence is not null)
        {
            evidenceAccessor.Record(evidence);
        }

        return new HttpContextApplicationAuditContextAccessor(
            new HttpContextAccessor { HttpContext = httpContext },
            evidenceAccessor);
    }

    private static ClaimsPrincipal CreateAuthenticatedPrincipal(params Claim[] claims)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }
}
