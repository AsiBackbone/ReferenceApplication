using System.Security.Claims;
using AsiBackbone.Core.Constraints;
using AsiBackbone.Core.Decisions;
using AsiBackbone.Core.Evaluation;
using Microsoft.AspNetCore.Http;
using ReferenceApplication.Web.Authentication.Claims;
using ReferenceApplication.Web.Authentication.Options;
using ReferenceApplication.Web.Governance;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies the host-owned governance rules introduced in walkthrough Step 4.
/// </summary>
public sealed class GovernancePolicyTests
{
    private const string _expectedPolicyHash =
        "sha256:75b889021f5c200e2c10bf03f0d6e00d906dc3b747bdac2796a1f961c0f03e33";

    /// <summary>
    /// Verifies that policy version 1.0.0 retains its reviewed canonical definition.
    /// </summary>
    [Fact]
    public void PolicySet_VersionOne_HasExpectedStableHash()
    {
        Assert.Equal("1.0.0", ReferenceApplicationGovernancePolicySet.Version);
        Assert.Equal(_expectedPolicyHash, ReferenceApplicationGovernancePolicySet.PolicyHash);
    }

    /// <summary>
    /// Verifies that an authenticated actor in a configured administrator role satisfies the administrator rule.
    /// </summary>
    [Fact]
    public async Task AdministratorRule_ConfiguredRole_Allows()
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = CreateHttpContext(new Claim(ApplicationClaimTypes.Role, "administrator"))
        };
        var constraint = new AdministratorRoleConstraint(
            accessor,
            Microsoft.Extensions.Options.Options.Create(new ApplicationAuthorizationOptions()));

        ConstraintEvaluationResult result = await constraint.EvaluateAsync(
            CreateContext<AdministratorActionPolicy>(),
            TestContext.Current.CancellationToken);

        Assert.True(result.CanProceed);
        Assert.False(result.IsDenied);
    }

    /// <summary>
    /// Verifies that an authenticated actor without a configured administrator role is denied with a stable reason.
    /// </summary>
    [Fact]
    public async Task AdministratorRule_NonAdministrator_Denies()
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = CreateHttpContext(new Claim(ApplicationClaimTypes.Role, "reader"))
        };
        var constraint = new AdministratorRoleConstraint(
            accessor,
            Microsoft.Extensions.Options.Options.Create(new ApplicationAuthorizationOptions()));

        ConstraintEvaluationResult result = await constraint.EvaluateAsync(
            CreateContext<AdministratorActionPolicy>(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsDenied);
        Assert.Equal(AdministratorRoleConstraint.DeniedReasonCode, Assert.Single(result.ReasonCodes));
    }

    /// <summary>
    /// Verifies that a role claim on an unauthenticated identity cannot satisfy the administrator rule.
    /// </summary>
    [Fact]
    public async Task AdministratorRule_UnauthenticatedAdministratorClaim_Denies()
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ApplicationClaimTypes.Role, "administrator")],
                    authenticationType: null))
            }
        };
        var constraint = new AdministratorRoleConstraint(
            accessor,
            Microsoft.Extensions.Options.Options.Create(new ApplicationAuthorizationOptions()));

        ConstraintEvaluationResult result = await constraint.EvaluateAsync(
            CreateContext<AdministratorActionPolicy>(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsDenied);
        Assert.Equal(AdministratorRoleConstraint.DeniedReasonCode, Assert.Single(result.ReasonCodes));
    }

    /// <summary>
    /// Verifies that the administrator constraint does not affect endpoints carrying a different policy marker.
    /// </summary>
    [Fact]
    public async Task AdministratorRule_UnrelatedPolicy_IsNotApplicable()
    {
        var constraint = new AdministratorRoleConstraint(
            new HttpContextAccessor(),
            Microsoft.Extensions.Options.Options.Create(new ApplicationAuthorizationOptions()));

        ConstraintEvaluationResult result = await constraint.EvaluateAsync(
            CreateContext<ConsequentialActionPolicy>(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsNotApplicable);
    }

    /// <summary>
    /// Verifies that capability-protected policy metadata must declare the policy set's required scope.
    /// </summary>
    [Fact]
    public async Task CapabilityRule_MissingRequiredScope_Denies()
    {
        var constraint = new CapabilityScopeDeclarationConstraint();

        ConstraintEvaluationResult result = await constraint.EvaluateAsync(
            CreateContext<CapabilityProtectedActionPolicy>(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsDenied);
        Assert.Equal(CapabilityScopeDeclarationConstraint.DeniedReasonCode, Assert.Single(result.ReasonCodes));
    }

    /// <summary>
    /// Verifies that a capability-protected policy with the required endpoint scope satisfies the declaration rule.
    /// </summary>
    [Fact]
    public async Task CapabilityRule_RequiredScopeDeclared_Allows()
    {
        var constraint = new CapabilityScopeDeclarationConstraint();
        GovernanceEvaluationContext context = CreateContext<CapabilityProtectedActionPolicy>(
            ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope);

        ConstraintEvaluationResult result = await constraint.EvaluateAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.True(result.CanProceed);
        Assert.False(result.IsDenied);
    }

    /// <summary>
    /// Verifies that an otherwise-allowed consequential action requires acknowledgment and retains policy identity.
    /// </summary>
    [Fact]
    public async Task ConsequentialRule_AllowedDecision_RequiresAcknowledgment()
    {
        var policy = new ConsequentialActionDecisionPolicy();
        GovernanceEvaluationContext context = CreateContext<ConsequentialActionPolicy>();

        GovernanceDecision result = await policy.ApplyAsync(
            context,
            GovernanceDecision.Allow(
                correlationId: context.CorrelationId,
                policyVersion: context.PolicyVersion,
                policyHash: context.PolicyHash),
            [],
            TestContext.Current.CancellationToken);

        Assert.True(result.RequiresAcknowledgment);
        Assert.Equal(ConsequentialActionDecisionPolicy.AcknowledgmentReasonCode, Assert.Single(result.ReasonCodes));
        Assert.Equal(ReferenceApplicationGovernancePolicySet.Version, result.PolicyVersion);
        Assert.Equal(ReferenceApplicationGovernancePolicySet.PolicyHash, result.PolicyHash);
    }

    /// <summary>
    /// Verifies that the consequential policy never weakens a blocking decision produced by a constraint.
    /// </summary>
    [Fact]
    public async Task ConsequentialRule_DeniedDecision_RemainsDenied()
    {
        var policy = new ConsequentialActionDecisionPolicy();
        GovernanceEvaluationContext context = CreateContext<ConsequentialActionPolicy>();
        var denied = GovernanceDecision.Deny("test.denied", "Denied by a test constraint.");

        GovernanceDecision result = await policy.ApplyAsync(
            context,
            denied,
            [],
            TestContext.Current.CancellationToken);

        Assert.Same(denied, result);
    }

    /// <summary>
    /// Verifies the composed evaluator denies an administrator operation before the consequential policy can require
    /// acknowledgment when the current actor lacks the required role.
    /// </summary>
    [Fact]
    public async Task Evaluator_AdministratorWithoutRole_DeniesBeforeAcknowledgment()
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = CreateHttpContext(new Claim(ApplicationClaimTypes.Role, "reader"))
        };
        DefaultGovernancePolicyEvaluator<GovernanceEvaluationContext> evaluator = CreateEvaluator(accessor);
        GovernanceEvaluationContext context = CreateContext(
            [typeof(AdministratorActionPolicy), typeof(ConsequentialActionPolicy)]);

        GovernanceDecision result = await evaluator.EvaluateAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsDenied);
        Assert.False(result.RequiresAcknowledgment);
        Assert.Equal(AdministratorRoleConstraint.DeniedReasonCode, Assert.Single(result.ReasonCodes));
    }

    /// <summary>
    /// Verifies the composed evaluator advances an allowed administrator operation to acknowledgment-required when
    /// the consequential marker is also present.
    /// </summary>
    [Fact]
    public async Task Evaluator_AdministratorWithRole_RequiresAcknowledgmentForConsequentialAction()
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = CreateHttpContext(new Claim(ApplicationClaimTypes.Role, "administrator"))
        };
        DefaultGovernancePolicyEvaluator<GovernanceEvaluationContext> evaluator = CreateEvaluator(accessor);
        GovernanceEvaluationContext context = CreateContext(
            [typeof(AdministratorActionPolicy), typeof(ConsequentialActionPolicy)]);

        GovernanceDecision result = await evaluator.EvaluateAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.True(result.RequiresAcknowledgment);
        Assert.Equal(ConsequentialActionDecisionPolicy.AcknowledgmentReasonCode, Assert.Single(result.ReasonCodes));
    }

    /// <summary>
    /// Verifies the composed evaluator allows a capability-protected policy when its required scope is declared.
    /// </summary>
    [Fact]
    public async Task Evaluator_CapabilityScopeDeclared_Allows()
    {
        DefaultGovernancePolicyEvaluator<GovernanceEvaluationContext> evaluator = CreateEvaluator(new HttpContextAccessor());
        GovernanceEvaluationContext context = CreateContext(
            [typeof(CapabilityProtectedActionPolicy)],
            ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope);

        GovernanceDecision result = await evaluator.EvaluateAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsAllowed);
    }

    private static GovernanceEvaluationContext CreateContext<TPolicy>(string? capabilityScope = null)
    {
        return CreateContext([typeof(TPolicy)], capabilityScope);
    }

    private static GovernanceEvaluationContext CreateContext(
        IReadOnlyList<Type> policyTypes,
        string? capabilityScope = null)
    {
        Dictionary<string, string> metadata = new(StringComparer.Ordinal)
        {
            ["endpoint.policy_types"] = string.Join(
                ',',
                policyTypes.Select(static policyType => policyType.FullName ?? policyType.Name))
        };

        if (capabilityScope is not null)
        {
            metadata["endpoint.capability_scopes"] = capabilityScope;
        }

        return new GovernanceEvaluationContext(
            correlationId: "test-correlation",
            policyVersion: ReferenceApplicationGovernancePolicySet.Version,
            policyHash: ReferenceApplicationGovernancePolicySet.PolicyHash,
            metadata: metadata);
    }

    private static DefaultGovernancePolicyEvaluator<GovernanceEvaluationContext> CreateEvaluator(
        IHttpContextAccessor httpContextAccessor)
    {
        var administratorConstraint = new AdministratorRoleConstraint(
            httpContextAccessor,
            Microsoft.Extensions.Options.Options.Create(new ApplicationAuthorizationOptions()));

        return DefaultGovernancePolicyEvaluator.CreateBuilder<GovernanceEvaluationContext>()
            .AddConstraint(administratorConstraint)
            .AddConstraint(new CapabilityScopeDeclarationConstraint())
            .WithDecisionPolicy(new ConsequentialActionDecisionPolicy())
            .Build();
    }

    private static DefaultHttpContext CreateHttpContext(params Claim[] claims)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"))
        };

        return context;
    }
}
