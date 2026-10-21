using System.Security.Claims;
using AsiBackbone.AspNetCore.Actors;
using AsiBackbone.AspNetCore.CapabilityGrants;
using AsiBackbone.AspNetCore.Endpoints;
using AsiBackbone.Core.Actors;
using AsiBackbone.Core.CapabilityGrants;
using AsiBackbone.Core.Constraints;
using AsiBackbone.Core.Decisions;
using AsiBackbone.Core.Evaluation;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ReferenceApplication.Web.Authentication.Options;
using ReferenceApplication.Web.Controllers.Api;

namespace ReferenceApplication.Web.Governance.Simulation;

/// <summary>
/// Simulates the governance evaluation of a sample governed operation and reports each stage.
/// </summary>
public interface IPolicySimulator
{
    /// <summary>
    /// Gets the names of the registered constraints, in evaluation order.
    /// </summary>
    IReadOnlyList<string> ConstraintNames { get; }

    /// <summary>
    /// Simulates a governed request without persisting anything.
    /// </summary>
    /// <param name="requestContext">The simulator request, whose signed-in actor the simulation starts from.</param>
    /// <param name="request">The simulation inputs.</param>
    /// <param name="cancellationToken">A token that cancels the simulation.</param>
    /// <returns>The stage-by-stage result, or the reason the request could not be simulated.</returns>
    ValueTask<PolicySimulationOutcome> SimulateAsync(
        HttpContext requestContext,
        PolicySimulationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs AsiBackbone's endpoint governance pipeline against a simulated request for a sample governed operation.
/// </summary>
/// <remarks>
/// <para>
/// The simulation does not reimplement governance. It resolves the real governed endpoint and runs it through
/// <see cref="GovernancePipelineHarness" />, which evaluates a simulated request with AsiBackbone's
/// <see cref="DefaultEndpointGovernanceService" />, the same service the endpoint governance middleware runs, and the
/// policy evaluator composed exactly as the registered one.
/// </para>
/// <para>
/// Nothing is persisted. The harness captures the decision receipt instead of writing it, and while capability tokens
/// are enabled, the signed-grant validator, which would consume a use, is replaced with a preview of the token
/// issuance would sign.
/// </para>
/// </remarks>
internal sealed class PolicySimulator : IPolicySimulator
{
    /// <summary>
    /// Gets the capability stage model reported when grants come from permission claims.
    /// </summary>
    public const string ClaimsModel = "claims";

    /// <summary>
    /// Gets the capability stage model reported when grants are signed capability tokens.
    /// </summary>
    public const string SignedTokenModel = "signed-token";

    private static readonly PolicySimulatorOperation[] _operations =
    [
        new("administrator", nameof(GovernedOperationsController.ExecuteAdministratorAction), "Note", RequiresAcknowledgmentFlag: false),
        new("capability", nameof(GovernedOperationsController.ExecuteCapabilityAction), "Note", RequiresAcknowledgmentFlag: false),
        new("consequential", nameof(GovernedOperationsController.ExecuteConsequentialAction), "Note", RequiresAcknowledgmentFlag: true),
        new("records", nameof(GovernedOperationsController.CreateRecord), "Title", RequiresAcknowledgmentFlag: false)
    ];

    private readonly IGovernanceConstraint<GovernanceEvaluationContext>[] _constraints;
    private readonly IGovernanceDecisionPolicy<GovernanceEvaluationContext> _decisionPolicy;
    private readonly ClaimEndpointCapabilityGrantValidator _claimValidator;
    private readonly GovernancePipelineHarness _harness;
    private readonly IOptions<ApplicationAuthorizationOptions> _authorizationOptions;
    private readonly IOptions<CapabilityTokenOptions> _tokenOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="PolicySimulator" /> class.
    /// </summary>
    /// <param name="constraints">The registered constraints, in evaluation order.</param>
    /// <param name="decisionPolicy">The registered decision policy.</param>
    /// <param name="claimValidator">The claim-backed capability validator, which also decides token entitlement.</param>
    /// <param name="harness">The pipeline harness that runs the simulated request.</param>
    /// <param name="authorizationOptions">The application authorization options.</param>
    /// <param name="tokenOptions">The capability token options.</param>
    public PolicySimulator(
        IEnumerable<IGovernanceConstraint<GovernanceEvaluationContext>> constraints,
        IGovernanceDecisionPolicy<GovernanceEvaluationContext> decisionPolicy,
        ClaimEndpointCapabilityGrantValidator claimValidator,
        GovernancePipelineHarness harness,
        IOptions<ApplicationAuthorizationOptions> authorizationOptions,
        IOptions<CapabilityTokenOptions> tokenOptions)
    {
        ArgumentNullException.ThrowIfNull(constraints);

        _constraints = [.. constraints];
        _decisionPolicy = decisionPolicy ?? throw new ArgumentNullException(nameof(decisionPolicy));
        _claimValidator = claimValidator ?? throw new ArgumentNullException(nameof(claimValidator));
        _harness = harness ?? throw new ArgumentNullException(nameof(harness));
        _authorizationOptions = authorizationOptions ?? throw new ArgumentNullException(nameof(authorizationOptions));
        _tokenOptions = tokenOptions ?? throw new ArgumentNullException(nameof(tokenOptions));
        ConstraintNames = [.. _constraints.Select(static constraint => constraint.Name)];
    }

    /// <summary>
    /// Gets the keys of the sample governed operations the simulator accepts.
    /// </summary>
    public static IReadOnlyList<string> OperationKeys { get; } = [.. _operations.Select(static operation => operation.Key)];

    /// <summary>
    /// Gets each sample governed operation's key and its <see cref="GovernedOperationsController" /> action name.
    /// </summary>
    internal static IEnumerable<(string Key, string ActionName)> GovernedOperations =>
        _operations.Select(static operation => (operation.Key, operation.ActionName));

    /// <inheritdoc />
    public IReadOnlyList<string> ConstraintNames { get; }

    /// <inheritdoc />
    public async ValueTask<PolicySimulationOutcome> SimulateAsync(
        HttpContext requestContext,
        PolicySimulationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestContext);
        ArgumentNullException.ThrowIfNull(request);

        PolicySimulatorOperation? operation = Array.Find(
            _operations,
            candidate => string.Equals(candidate.Key, request.Operation?.Trim(), StringComparison.Ordinal));
        if (operation is null)
        {
            return PolicySimulationOutcome.Invalid(
                $"Choose one of the sample operations: {string.Join(", ", OperationKeys)}.");
        }

        if (operation.RequiresAcknowledgmentFlag && !request.Acknowledged)
        {
            return PolicySimulationOutcome.Invalid(
                "Set the acknowledgment flag first. The explorer sends the consequential request only when it is set.");
        }

        string payload = request.Payload?.Trim() ?? string.Empty;
        if (payload.Length > PolicySimulatorOptions.MaximumPayloadLength)
        {
            return PolicySimulationOutcome.Invalid(
                $"The sample payload must be at most {PolicySimulatorOptions.MaximumPayloadLength} characters.");
        }

        if (string.Equals(operation.PayloadField, "Title", StringComparison.Ordinal) && payload.Length == 0)
        {
            return PolicySimulationOutcome.Invalid("Enter a sample payload to use as the record title.");
        }

        HashSet<string> disabled = new(StringComparer.Ordinal);
        foreach (string name in request.DisabledConstraints)
        {
            string trimmed = name.Trim();
            if (!ConstraintNames.Contains(trimmed, StringComparer.Ordinal))
            {
                return PolicySimulationOutcome.Invalid($"'{trimmed}' is not a registered constraint.");
            }

            _ = disabled.Add(trimmed);
        }

        Endpoint endpoint = _harness.FindGovernedEndpoint(operation.ActionName)
            ?? throw new InvalidOperationException($"The governed operation endpoint '{operation.ActionName}' is not mapped.");

        Dictionary<string, StringValues> fields = new(StringComparer.Ordinal);
        if (payload.Length > 0)
        {
            fields[operation.PayloadField] = payload;
        }

        bool tokensEnabled = _tokenOptions.Value.Enabled;
        ClaimsPrincipal actor = CreateSimulatedActor(
            requestContext.User,
            request.Administrator,
            request.ConsequentialCapability);

        GovernancePipelineRun run = await _harness.RunAsync(
            new GovernancePipelineRequest(
                RequestContext: requestContext,
                Endpoint: endpoint,
                RequestPath: $"/api/v1/governed-operations/{operation.Key}",
                Actor: actor,
                Fields: fields,
                Constraints: _constraints,
                DisabledConstraints: disabled,
                DecisionPolicy: _decisionPolicy,
                CreateCapabilityValidator: recorder => tokensEnabled
                    ? new TokenPreviewCapabilityValidator(
                        _claimValidator,
                        _harness.ActorContextResolver,
                        requestContext.RequestServices.GetRequiredService<CapabilityTokenIssuer>(),
                        requestContext.RequestServices.GetRequiredService<CapabilityTokenSigningKey>(),
                        fields,
                        recorder)
                    : GovernancePipelineHarness.Record(_claimValidator, recorder),
                TraceIdentifierPrefix: "policy-simulator"),
            cancellationToken).ConfigureAwait(false);
        EndpointGovernanceDescriptor descriptor = run.Descriptor;

        bool matchesSignedInActor =
            IsAdministrator(requestContext.User) == request.Administrator &&
            HasConsequentialCapability(requestContext.User) == request.ConsequentialCapability;

        return new PolicySimulationOutcome(
            new PolicySimulationResult(
                Operation: operation.Key,
                OperationName: descriptor.OperationName,
                SimulatedWithModifiedPolicy: disabled.Count > 0,
                DisabledConstraints: [.. ConstraintNames.Where(disabled.Contains)],
                Actor: new PolicySimulationActor(
                    ActorId: run.Receipt?.ActorId ?? string.Empty,
                    DisplayName: run.Receipt?.ActorDisplayName,
                    Administrator: request.Administrator,
                    ConsequentialCapability: request.ConsequentialCapability,
                    MatchesSignedInActor: matchesSignedInActor),
                Payload: fields.ToDictionary(
                    static field => field.Key,
                    static field => field.Value.ToString(),
                    StringComparer.Ordinal),
                Constraints: GovernancePipelineHarness.CreateConstraintStages(run.Recorder, disabled),
                DecisionPolicy: GovernancePipelineHarness.CreateDecisionPolicyStage(run.Recorder),
                Capability: GovernancePipelineHarness.CreateCapabilityStage(
                    descriptor,
                    run.Recorder,
                    tokensEnabled ? SignedTokenModel : ClaimsModel),
                Receipt: GovernancePipelineHarness.CreateReceipt(run.Receipt),
                EndpointResult: _harness.CreateEndpointResult(run.Result),
                SendForReal: CreateSendForReal(disabled.Count > 0, matchesSignedInActor, tokensEnabled, descriptor)),
            Error: null);
    }

    private static PolicySimulationSendForReal CreateSendForReal(
        bool modifiedPolicy,
        bool matchesSignedInActor,
        bool tokensEnabled,
        EndpointGovernanceDescriptor descriptor)
    {
        return modifiedPolicy
            ? new PolicySimulationSendForReal(
                Available: false,
                Reason: "The simulation left constraints out. The real endpoint always applies the full policy.")
            : !matchesSignedInActor
            ? new PolicySimulationSendForReal(
                Available: false,
                Reason: "The simulated role or capability differs from the signed-in actor. Apply a matching demo identity first.")
            : tokensEnabled && descriptor.CapabilityScopes.Count > 0
            ? new PolicySimulationSendForReal(
                Available: false,
                Reason: "Capability tokens are enabled, and the explorer does not request a token for this operation yet.")
            : new PolicySimulationSendForReal(Available: true, Reason: null);
    }

    private ClaimsPrincipal CreateSimulatedActor(ClaimsPrincipal signedInActor, bool administrator, bool capability)
    {
        // Start from the signed-in actor, so the actor identifier and every other claim are exactly what a real
        // request would carry, then replace only the administrator role and the capability claim.
        ApplicationAuthorizationOptions options = _authorizationOptions.Value;
        ClaimsIdentity? source = signedInActor.Identities.FirstOrDefault(static identity => identity.IsAuthenticated);
        var identity = new ClaimsIdentity(
            source?.Claims.Where(claim => !IsAdministratorRoleClaim(claim, options) && !IsCapabilityClaim(claim, options))
                ?? [],
            source?.AuthenticationType ?? "PolicySimulator",
            source?.NameClaimType ?? ClaimsIdentity.DefaultNameClaimType,
            source?.RoleClaimType ?? ClaimsIdentity.DefaultRoleClaimType);

        if (administrator && options.AdministratorRoles.Length > 0)
        {
            identity.AddClaim(new Claim(options.RoleClaimType, options.AdministratorRoles[0]));
        }

        if (capability)
        {
            identity.AddClaim(new Claim(
                options.PermissionClaimType,
                ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope));
        }

        return new ClaimsPrincipal(identity);
    }

    private bool IsAdministrator(ClaimsPrincipal actor)
    {
        ApplicationAuthorizationOptions options = _authorizationOptions.Value;
        return actor.Identities
            .Where(static identity => identity.IsAuthenticated)
            .SelectMany(static identity => identity.Claims)
            .Any(claim => IsAdministratorRoleClaim(claim, options));
    }

    private bool HasConsequentialCapability(ClaimsPrincipal actor)
    {
        ApplicationAuthorizationOptions options = _authorizationOptions.Value;
        return actor.Identities
            .Where(static identity => identity.IsAuthenticated)
            .SelectMany(static identity => identity.Claims)
            .Any(claim => IsCapabilityClaim(claim, options));
    }

    private static bool IsAdministratorRoleClaim(Claim claim, ApplicationAuthorizationOptions options)
    {
        return string.Equals(claim.Type, options.RoleClaimType, StringComparison.Ordinal)
            && options.AdministratorRoles.Contains(claim.Value, StringComparer.Ordinal);
    }

    private static bool IsCapabilityClaim(Claim claim, ApplicationAuthorizationOptions options)
    {
        return string.Equals(claim.Type, options.PermissionClaimType, StringComparison.Ordinal)
            && string.Equals(claim.Value, ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope, StringComparison.Ordinal);
    }

    private sealed record PolicySimulatorOperation(
        string Key,
        string ActionName,
        string PayloadField,
        bool RequiresAcknowledgmentFlag);

    private sealed class TokenPreviewCapabilityValidator(
        ClaimEndpointCapabilityGrantValidator entitlementValidator,
        IHttpGovernanceActorContextResolver actorContextResolver,
        CapabilityTokenIssuer issuer,
        CapabilityTokenSigningKey signingKey,
        Dictionary<string, StringValues> fields,
        GovernanceStageRecorder recorder) : IEndpointCapabilityGrantValidator
    {
        public async ValueTask<GovernanceDecision> ValidateAsync(
            HttpContext httpContext,
            EndpointGovernanceDescriptor descriptor,
            GovernanceDecision currentDecision,
            CancellationToken cancellationToken = default)
        {
            recorder.CapabilityInput = currentDecision;

            // Issuance checks entitlement with the claim validator, exactly as POST /api/v1/capability-tokens does. An
            // actor without it receives no token, so the real request would arrive without one.
            GovernanceDecision entitlement = await entitlementValidator
                .ValidateAsync(httpContext, descriptor, GovernanceDecision.Allow(), cancellationToken)
                .ConfigureAwait(false);
            IGovernanceActorContext actor = actorContextResolver.ResolveActorContext();

            GovernanceDecision decision;
            if (!actor.IsAuthenticated || entitlement.IsDenied)
            {
                recorder.TokenPreview = new PolicySimulationTokenPreview(
                    WouldBeIssued: false,
                    TokenId: null,
                    Issuer: null,
                    Audience: null,
                    SubjectId: actor.ActorId,
                    OperationName: descriptor.OperationName,
                    Scopes: descriptor.CapabilityScopes,
                    ResourceBinding: null,
                    IssuedUtc: null,
                    NotBeforeUtc: null,
                    ExpiresUtc: null,
                    MaxUseCount: null,
                    PolicyVersion: null,
                    PolicyHash: null,
                    KeyId: signingKey.KeyId,
                    KeyVersion: signingKey.KeyVersion,
                    SignatureAlgorithm: CapabilityTokenSigningKey.SignatureAlgorithm);
                decision = GovernanceDecision.Deny(
                    SignedCapabilityGrantReasonCodes.Missing,
                    "No capability token would be issued to this actor, so the request would carry none.",
                    correlationId: currentDecision.CorrelationId,
                    traceId: currentDecision.TraceId,
                    policyVersion: currentDecision.PolicyVersion,
                    policyHash: currentDecision.PolicyHash);
            }
            else
            {
                CapabilityGrant grant = issuer.CreateGrant(new CapabilityTokenIssueRequest(
                    actor.ActorId,
                    descriptor.OperationName,
                    descriptor.CapabilityScopes,
                    [.. fields]));
                recorder.TokenPreview = new PolicySimulationTokenPreview(
                    WouldBeIssued: true,
                    TokenId: grant.TokenId,
                    Issuer: grant.Issuer,
                    Audience: grant.Audience,
                    SubjectId: grant.SubjectId,
                    OperationName: grant.OperationName,
                    Scopes: grant.Scopes,
                    ResourceBinding: grant.ResourceBinding,
                    IssuedUtc: grant.IssuedUtc,
                    NotBeforeUtc: grant.NotBeforeUtc,
                    ExpiresUtc: grant.ExpiresUtc,
                    MaxUseCount: grant.MaxUseCount,
                    PolicyVersion: grant.PolicyVersion,
                    PolicyHash: grant.PolicyHash,
                    KeyId: signingKey.KeyId,
                    KeyVersion: signingKey.KeyVersion,
                    SignatureAlgorithm: CapabilityTokenSigningKey.SignatureAlgorithm);

                // A valid token preserves the current decision and names itself on the receipt, as AsiBackbone's
                // signed-grant validator does. No use is consumed.
                httpContext.Features.Set<IEndpointCapabilityGrantFeature>(new EndpointCapabilityGrantFeature(grant.TokenId));
                decision = currentDecision;
            }

            recorder.CapabilityOutput = decision;
            return decision;
        }
    }
}
