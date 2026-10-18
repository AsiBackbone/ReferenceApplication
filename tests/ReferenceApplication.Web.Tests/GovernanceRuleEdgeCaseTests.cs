using System.Security.Claims;
using AsiBackbone.AspNetCore.Endpoints;
using AsiBackbone.Core.Constraints;
using AsiBackbone.Core.Decisions;
using Microsoft.AspNetCore.Http;
using ReferenceApplication.Web.Authentication.Claims;
using ReferenceApplication.Web.Authentication.Options;
using ReferenceApplication.Web.Governance;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Covers edge cases of the walkthrough Step 4 governance rules and the Step 5 capability validator that the original
/// step tests did not exercise.
/// </summary>
public sealed class GovernanceRuleEdgeCaseTests
{
    private static readonly string _administratorPolicy = typeof(AdministratorActionPolicy).FullName!;

    /// <summary>
    /// Verifies how policy markers are parsed from the comma-separated endpoint metadata.
    /// </summary>
    /// <param name="policyTypes">The raw <c>endpoint.policy_types</c> value.</param>
    /// <param name="expected">Whether the administrator marker is recognized.</param>
    [Theory]
    [InlineData("ReferenceApplication.Web.Governance.AdministratorActionPolicy", true)]
    [InlineData(" ReferenceApplication.Web.Governance.AdministratorActionPolicy , Other.Policy ", true)]
    [InlineData("Other.Policy,ReferenceApplication.Web.Governance.AdministratorActionPolicy", true)]
    [InlineData("Other.Policy,,", false)]
    [InlineData("referenceapplication.web.governance.administratoractionpolicy", false)]
    [InlineData("ReferenceApplication.Web.Governance.AdministratorActionPolicyExtended", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void ContainsPolicy_ParsesCommaSeparatedMarkers(string policyTypes, bool expected)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["endpoint.policy_types"] = policyTypes };

        Assert.Equal(expected, GovernanceMetadata.ContainsPolicy<AdministratorActionPolicy>(metadata));
    }

    /// <summary>
    /// Verifies that missing metadata entries are treated as absent and that invalid arguments are rejected.
    /// </summary>
    [Fact]
    public void GovernanceMetadata_MissingEntriesAndInvalidArguments()
    {
        var empty = new Dictionary<string, string>(StringComparer.Ordinal);

        Assert.False(GovernanceMetadata.ContainsPolicy<AdministratorActionPolicy>(empty));
        Assert.False(GovernanceMetadata.ContainsCapabilityScope(empty, "scope"));
        _ = Assert.Throws<ArgumentNullException>(() => GovernanceMetadata.ContainsPolicy<AdministratorActionPolicy>(null!));
        _ = Assert.Throws<ArgumentNullException>(() => GovernanceMetadata.ContainsCapabilityScope(null!, "scope"));
        _ = Assert.Throws<ArgumentException>(() => GovernanceMetadata.ContainsCapabilityScope(empty, " "));
    }

    /// <summary>
    /// Verifies that a role claim on an unauthenticated identity does not count even when the principal also has an
    /// authenticated identity without the role.
    /// </summary>
    [Fact]
    public async Task AdministratorRule_RoleOnlyOnUnauthenticatedSecondaryIdentity_Denies()
    {
        var principal = new ClaimsPrincipal(
        [
            new ClaimsIdentity([new Claim(ApplicationClaimTypes.Role, "reader")], authenticationType: "Test"),
            new ClaimsIdentity([new Claim(ApplicationClaimTypes.Role, "administrator")], authenticationType: null)
        ]);

        ConstraintEvaluationResult result = await EvaluateAdministratorAsync(principal, new ApplicationAuthorizationOptions());

        Assert.True(result.IsDenied);
    }

    /// <summary>
    /// Verifies that the rule follows the configured role claim type and role list, matching role values exactly.
    /// </summary>
    /// <param name="claimType">The role claim type on the principal.</param>
    /// <param name="role">The role value on the principal.</param>
    /// <param name="expectedAllowed">Whether the actor is allowed.</param>
    [Theory]
    [InlineData("custom:role", "governance-admin", true)]
    [InlineData("custom:role", "Governance-Admin", false)]
    [InlineData(ApplicationClaimTypes.Role, "governance-admin", false)]
    [InlineData("custom:role", "administrator", false)]
    public async Task AdministratorRule_UsesConfiguredRoleClaimTypeAndRoles(string claimType, string role, bool expectedAllowed)
    {
        var options = new ApplicationAuthorizationOptions
        {
            RoleClaimType = "custom:role",
            AdministratorRoles = ["governance-admin"]
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(claimType, role)], authenticationType: "Test"));

        ConstraintEvaluationResult result = await EvaluateAdministratorAsync(principal, options);

        Assert.Equal(expectedAllowed, result.CanProceed && !result.IsDenied);
    }

    /// <summary>
    /// Verifies that the administrator rule denies when there is no HTTP request.
    /// </summary>
    [Fact]
    public async Task AdministratorRule_NoHttpContext_Denies()
    {
        var constraint = new AdministratorRoleConstraint(
            new HttpContextAccessor(),
            Microsoft.Extensions.Options.Options.Create(new ApplicationAuthorizationOptions()));

        ConstraintEvaluationResult result = await constraint.EvaluateAsync(
            CreateContext(_administratorPolicy),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsDenied);
        Assert.Equal(AdministratorRoleConstraint.ConstraintName, constraint.Name);
    }

    /// <summary>
    /// Verifies the capability declaration rule's not-applicable path and stable name.
    /// </summary>
    [Fact]
    public async Task CapabilityRule_UnrelatedPolicy_IsNotApplicable()
    {
        var constraint = new CapabilityScopeDeclarationConstraint();

        ConstraintEvaluationResult result = await constraint.EvaluateAsync(
            CreateContext(_administratorPolicy),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsNotApplicable);
        Assert.Equal(CapabilityScopeDeclarationConstraint.ConstraintName, constraint.Name);
    }

    /// <summary>
    /// Verifies that an allowed decision carrying a warning is still converted to acknowledgment-required for a
    /// consequential operation, so a warning cannot bypass acknowledgment.
    /// </summary>
    [Fact]
    public async Task ConsequentialRule_WarningDecision_RequiresAcknowledgment()
    {
        GovernanceDecision result = await new ConsequentialActionDecisionPolicy().ApplyAsync(
            CreateContext(typeof(ConsequentialActionPolicy).FullName!),
            GovernanceDecision.Warning("test.warning", "A warning."),
            [],
            TestContext.Current.CancellationToken);

        Assert.True(result.RequiresAcknowledgment);
    }

    /// <summary>
    /// Verifies that the consequential rule leaves decisions for other endpoints unchanged.
    /// </summary>
    [Fact]
    public async Task ConsequentialRule_UnrelatedPolicy_PreservesDecision()
    {
        var allowed = GovernanceDecision.Allow();

        GovernanceDecision result = await new ConsequentialActionDecisionPolicy().ApplyAsync(
            CreateContext(_administratorPolicy),
            allowed,
            [],
            TestContext.Current.CancellationToken);

        Assert.Same(allowed, result);
    }

    /// <summary>
    /// Verifies argument validation and cancellation across the governance rules.
    /// </summary>
    [Fact]
    public async Task Rules_ValidateArgumentsAndCancellation()
    {
        var administrator = new AdministratorRoleConstraint(
            new HttpContextAccessor(),
            Microsoft.Extensions.Options.Options.Create(new ApplicationAuthorizationOptions()));
        var capability = new CapabilityScopeDeclarationConstraint();
        var consequential = new ConsequentialActionDecisionPolicy();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        GovernanceEvaluationContext context = CreateContext(_administratorPolicy);

        _ = Assert.Throws<ArgumentNullException>(() => new AdministratorRoleConstraint(
            null!, Microsoft.Extensions.Options.Options.Create(new ApplicationAuthorizationOptions())));
        _ = Assert.Throws<ArgumentNullException>(() => new AdministratorRoleConstraint(new HttpContextAccessor(), null!));
        _ = await Assert.ThrowsAsync<ArgumentNullException>(async () => await administrator.EvaluateAsync(null!, TestContext.Current.CancellationToken));
        _ = await Assert.ThrowsAsync<ArgumentNullException>(async () => await capability.EvaluateAsync(null!, TestContext.Current.CancellationToken));
        _ = await Assert.ThrowsAsync<OperationCanceledException>(async () => await administrator.EvaluateAsync(context, cancelled.Token));
        _ = await Assert.ThrowsAsync<OperationCanceledException>(async () => await capability.EvaluateAsync(context, cancelled.Token));
        _ = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await consequential.ApplyAsync(null!, GovernanceDecision.Allow(), [], TestContext.Current.CancellationToken));
        _ = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await consequential.ApplyAsync(context, null!, [], TestContext.Current.CancellationToken));
        _ = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await consequential.ApplyAsync(context, GovernanceDecision.Allow(), null!, TestContext.Current.CancellationToken));
        _ = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await consequential.ApplyAsync(context, GovernanceDecision.Allow(), [], cancelled.Token));
    }

    /// <summary>
    /// Verifies the Step 5 capability validator's argument validation and cancellation.
    /// </summary>
    [Fact]
    public async Task CapabilityValidator_ValidatesArgumentsAndCancellation()
    {
        var validator = new ClaimEndpointCapabilityGrantValidator(
            Microsoft.Extensions.Options.Options.Create(new ApplicationAuthorizationOptions()));
        var descriptor = EndpointGovernanceDescriptor.FromEndpoint(new Endpoint(
            static _ => Task.CompletedTask,
            new EndpointMetadataCollection(new RequireCapabilityGrantAttribute("scope")),
            "validator edge cases"));
        var httpContext = new DefaultHttpContext();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        _ = Assert.Throws<ArgumentNullException>(() => new ClaimEndpointCapabilityGrantValidator(null!));
        _ = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await validator.ValidateAsync(null!, descriptor, GovernanceDecision.Allow(), TestContext.Current.CancellationToken));
        _ = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await validator.ValidateAsync(httpContext, null!, GovernanceDecision.Allow(), TestContext.Current.CancellationToken));
        _ = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await validator.ValidateAsync(httpContext, descriptor, null!, TestContext.Current.CancellationToken));
        _ = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await validator.ValidateAsync(httpContext, descriptor, GovernanceDecision.Allow(), cancelled.Token));
    }

    /// <summary>
    /// Verifies the policy set's shared scope constant and that the hash is a SHA-256 value.
    /// </summary>
    [Fact]
    public void PolicySet_ExposesStableScopeAndHashFormat()
    {
        Assert.Equal("referenceapplication.consequential.execute", ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope);
        Assert.Matches("^sha256:[0-9a-f]{64}$", ReferenceApplicationGovernancePolicySet.PolicyHash);
    }

    private static async Task<ConstraintEvaluationResult> EvaluateAdministratorAsync(
        ClaimsPrincipal principal,
        ApplicationAuthorizationOptions options)
    {
        var constraint = new AdministratorRoleConstraint(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } },
            Microsoft.Extensions.Options.Options.Create(options));

        return await constraint.EvaluateAsync(CreateContext(_administratorPolicy), TestContext.Current.CancellationToken);
    }

    private static GovernanceEvaluationContext CreateContext(string policyTypes)
    {
        return new GovernanceEvaluationContext(
            correlationId: "test-correlation",
            policyVersion: ReferenceApplicationGovernancePolicySet.Version,
            policyHash: ReferenceApplicationGovernancePolicySet.PolicyHash,
            metadata: new Dictionary<string, string>(StringComparer.Ordinal) { ["endpoint.policy_types"] = policyTypes });
    }
}
