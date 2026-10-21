using System.Security.Claims;
using AsiBackbone.AspNetCore.Acknowledgments;
using AsiBackbone.AspNetCore.Actors;
using AsiBackbone.AspNetCore.CapabilityGrants;
using AsiBackbone.AspNetCore.Correlation;
using AsiBackbone.AspNetCore.Endpoints;
using AsiBackbone.AspNetCore.Results;
using AsiBackbone.Core.Actors;
using AsiBackbone.Core.Audit;
using AsiBackbone.Core.CapabilityGrants;
using AsiBackbone.Core.Constraints;
using AsiBackbone.Core.Decisions;
using AsiBackbone.Core.Evaluation;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ReferenceApplication.Web.Authentication.Options;
using ReferenceApplication.Web.Controllers.Api;
using ReferenceApplication.Web.Extensions;

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
/// The simulation does not reimplement governance. It resolves the real governed endpoint, builds a simulated request
/// for it with the chosen actor and payload, and runs AsiBackbone's <see cref="DefaultEndpointGovernanceService" />,
/// the same service the endpoint governance middleware runs. The policy evaluator is composed through
/// <see cref="AsiBackboneServiceExtensions.CreatePolicyEvaluator" />, exactly as the registered evaluator is. Each
/// constraint, the decision policy, and the capability validator are wrapped only to record what they return.
/// </para>
/// <para>
/// Nothing is persisted. The decision receipt sink is replaced with one that captures the receipt instead of writing
/// it, and while capability tokens are enabled, the signed-grant validator, which would consume a use, is replaced
/// with a preview of the token issuance would sign. The simulated request runs on an isolated asynchronous flow, so
/// the services that read the current request through <see cref="IHttpContextAccessor" /> see the simulated request
/// and never the real one.
/// </para>
/// </remarks>
public sealed class PolicySimulator : IPolicySimulator
{
    private const string _claimsModel = "claims";
    private const string _signedTokenModel = "signed-token";

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
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IHttpGovernanceActorContextResolver _actorContextResolver;
    private readonly IHttpGovernanceRequestCorrelationResolver _correlationResolver;
    private readonly IAcknowledgmentChallengeService _acknowledgmentChallengeService;
    private readonly IOptions<EndpointGovernanceOptions> _endpointOptions;
    private readonly IOptions<GovernanceHttpResultMappingOptions> _resultOptions;
    private readonly IOptions<ApplicationAuthorizationOptions> _authorizationOptions;
    private readonly IOptions<CapabilityTokenOptions> _tokenOptions;
    private readonly EndpointDataSource _endpointDataSource;
    private readonly ILogger<DefaultGovernancePolicyEvaluator<GovernanceEvaluationContext>>? _evaluatorLogger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PolicySimulator" /> class.
    /// </summary>
    /// <param name="constraints">The registered constraints, in evaluation order.</param>
    /// <param name="decisionPolicy">The registered decision policy.</param>
    /// <param name="claimValidator">The claim-backed capability validator, which also decides token entitlement.</param>
    /// <param name="httpContextAccessor">The accessor the governance services read the current request through.</param>
    /// <param name="actorContextResolver">AsiBackbone's actor resolver.</param>
    /// <param name="correlationResolver">AsiBackbone's request correlation resolver.</param>
    /// <param name="acknowledgmentChallengeService">AsiBackbone's acknowledgment challenge service.</param>
    /// <param name="endpointOptions">The endpoint governance options.</param>
    /// <param name="resultOptions">The governance HTTP result mapping options.</param>
    /// <param name="authorizationOptions">The application authorization options.</param>
    /// <param name="tokenOptions">The capability token options.</param>
    /// <param name="endpointDataSource">The application's endpoints.</param>
    /// <param name="evaluatorLogger">The optional evaluator logger.</param>
    public PolicySimulator(
        IEnumerable<IGovernanceConstraint<GovernanceEvaluationContext>> constraints,
        IGovernanceDecisionPolicy<GovernanceEvaluationContext> decisionPolicy,
        ClaimEndpointCapabilityGrantValidator claimValidator,
        IHttpContextAccessor httpContextAccessor,
        IHttpGovernanceActorContextResolver actorContextResolver,
        IHttpGovernanceRequestCorrelationResolver correlationResolver,
        IAcknowledgmentChallengeService acknowledgmentChallengeService,
        IOptions<EndpointGovernanceOptions> endpointOptions,
        IOptions<GovernanceHttpResultMappingOptions> resultOptions,
        IOptions<ApplicationAuthorizationOptions> authorizationOptions,
        IOptions<CapabilityTokenOptions> tokenOptions,
        EndpointDataSource endpointDataSource,
        ILogger<DefaultGovernancePolicyEvaluator<GovernanceEvaluationContext>>? evaluatorLogger = null)
    {
        ArgumentNullException.ThrowIfNull(constraints);

        _constraints = [.. constraints];
        _decisionPolicy = decisionPolicy ?? throw new ArgumentNullException(nameof(decisionPolicy));
        _claimValidator = claimValidator ?? throw new ArgumentNullException(nameof(claimValidator));
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _actorContextResolver = actorContextResolver ?? throw new ArgumentNullException(nameof(actorContextResolver));
        _correlationResolver = correlationResolver ?? throw new ArgumentNullException(nameof(correlationResolver));
        _acknowledgmentChallengeService = acknowledgmentChallengeService
            ?? throw new ArgumentNullException(nameof(acknowledgmentChallengeService));
        _endpointOptions = endpointOptions ?? throw new ArgumentNullException(nameof(endpointOptions));
        _resultOptions = resultOptions ?? throw new ArgumentNullException(nameof(resultOptions));
        _authorizationOptions = authorizationOptions ?? throw new ArgumentNullException(nameof(authorizationOptions));
        _tokenOptions = tokenOptions ?? throw new ArgumentNullException(nameof(tokenOptions));
        _endpointDataSource = endpointDataSource ?? throw new ArgumentNullException(nameof(endpointDataSource));
        _evaluatorLogger = evaluatorLogger;
        ConstraintNames = [.. _constraints.Select(static constraint => constraint.Name)];
    }

    /// <summary>
    /// Gets the keys of the sample governed operations the simulator accepts.
    /// </summary>
    public static IReadOnlyList<string> OperationKeys { get; } = [.. _operations.Select(static operation => operation.Key)];

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

        Endpoint endpoint = FindGovernedEndpoint(operation.ActionName)
            ?? throw new InvalidOperationException($"The governed operation endpoint '{operation.ActionName}' is not mapped.");
        var descriptor = EndpointGovernanceDescriptor.FromEndpoint(endpoint);

        Dictionary<string, StringValues> fields = new(StringComparer.Ordinal);
        if (payload.Length > 0)
        {
            fields[operation.PayloadField] = payload;
        }

        bool tokensEnabled = _tokenOptions.Value.Enabled;
        var recorder = new SimulationRecorder(ConstraintNames);
        IGovernancePolicyEvaluator<GovernanceEvaluationContext> evaluator = AsiBackboneServiceExtensions.CreatePolicyEvaluator(
            _constraints
                .Select(static (constraint, index) => (constraint, index))
                .Where(entry => !disabled.Contains(entry.constraint.Name))
                .Select(entry => new RecordingConstraint(entry.constraint, entry.index, recorder)),
            new RecordingDecisionPolicy(_decisionPolicy, recorder),
            _evaluatorLogger);
        IEndpointCapabilityGrantValidator capabilityValidator = tokensEnabled
            ? new TokenPreviewCapabilityValidator(
                _claimValidator,
                _actorContextResolver,
                requestContext.RequestServices.GetRequiredService<CapabilityTokenIssuer>(),
                requestContext.RequestServices.GetRequiredService<CapabilityTokenSigningKey>(),
                fields,
                recorder)
            : new RecordingCapabilityValidator(_claimValidator, recorder);
        var receiptSink = new CapturingDecisionReceiptSink();
        var services = new SimulationServiceProvider(
            requestContext.RequestServices,
            evaluator,
            capabilityValidator,
            receiptSink);

        ClaimsPrincipal actor = CreateSimulatedActor(
            requestContext.User,
            request.Administrator,
            request.ConsequentialCapability);
        HttpContext simulated = CreateSimulatedRequest(requestContext, services, actor, endpoint, operation, fields);

        // A new instance, so the receipt sink it resolves is the capturing one; every other dependency is the
        // application's own.
        var governance = new DefaultEndpointGovernanceService(
            services,
            _actorContextResolver,
            _correlationResolver,
            _acknowledgmentChallengeService,
            _endpointOptions,
            _resultOptions);

        EndpointGovernanceResult result = await RunOnSimulatedRequestAsync(
            simulated,
            () => governance.EvaluateAsync(simulated, descriptor, cancellationToken).AsTask())
            .ConfigureAwait(false);

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
                    ActorId: receiptSink.Receipt?.ActorId ?? string.Empty,
                    DisplayName: receiptSink.Receipt?.ActorDisplayName,
                    Administrator: request.Administrator,
                    ConsequentialCapability: request.ConsequentialCapability,
                    MatchesSignedInActor: matchesSignedInActor),
                Payload: fields.ToDictionary(
                    static field => field.Key,
                    static field => field.Value.ToString(),
                    StringComparer.Ordinal),
                Constraints: CreateConstraintStages(recorder, disabled),
                DecisionPolicy: CreateDecisionPolicyStage(recorder),
                Capability: CreateCapabilityStage(descriptor, recorder, tokensEnabled),
                Receipt: CreateReceipt(receiptSink.Receipt),
                EndpointResult: CreateEndpointResult(result),
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

    private static List<PolicySimulationConstraintStage> CreateConstraintStages(
        SimulationRecorder recorder,
        HashSet<string> disabled)
    {
        List<PolicySimulationConstraintStage> stages = [];
        for (int index = 0; index < recorder.ConstraintNames.Count; index++)
        {
            ConstraintEvaluationResult? result = recorder.ConstraintResults[index];
            string name = recorder.ConstraintNames[index];
            stages.Add(new PolicySimulationConstraintStage(
                Order: index + 1,
                Name: name,
                Disabled: disabled.Contains(name),
                Evaluated: result is not null,
                Outcome: result?.Outcome.ToString(),
                ReasonCodes: result?.ReasonCodes ?? []));
        }

        return stages;
    }

    private static PolicySimulationDecisionPolicyStage CreateDecisionPolicyStage(SimulationRecorder recorder)
    {
        GovernanceDecision? composed = recorder.ComposedDecision;
        GovernanceDecision? final = recorder.PolicyDecision;

        return new PolicySimulationDecisionPolicyStage(
            Evaluated: final is not null,
            ComposedOutcome: composed?.Outcome.ToString(),
            ComposedReasonCodes: composed?.ReasonCodes ?? [],
            Outcome: final?.Outcome.ToString(),
            ReasonCodes: final?.ReasonCodes ?? [],
            Changed: composed is not null && final is not null && !ReferenceEquals(composed, final));
    }

    private static PolicySimulationCapabilityStage CreateCapabilityStage(
        EndpointGovernanceDescriptor descriptor,
        SimulationRecorder recorder,
        bool tokensEnabled)
    {
        return new PolicySimulationCapabilityStage(
            Applies: descriptor.CapabilityScopes.Count > 0,
            Model: tokensEnabled ? _signedTokenModel : _claimsModel,
            Evaluated: recorder.CapabilityOutput is not null,
            RequiredScopes: descriptor.CapabilityScopes,
            InputOutcome: recorder.CapabilityInput?.Outcome.ToString(),
            Outcome: recorder.CapabilityOutput?.Outcome.ToString(),
            ReasonCodes: recorder.CapabilityOutput?.ReasonCodes ?? [],
            Token: recorder.TokenPreview);
    }

    private static PolicySimulationReceipt? CreateReceipt(IDecisionReceipt? receipt)
    {
        return receipt is null
            ? null
            : new PolicySimulationReceipt(
                DecisionReceiptId: receipt.DecisionReceiptId ?? receipt.EventId,
                EventId: receipt.EventId,
                SchemaVersion: receipt.SchemaVersion,
                OccurredUtc: receipt.OccurredUtc,
                ActorId: receipt.ActorId,
                ActorType: receipt.ActorType.ToString(),
                ActorDisplayName: receipt.ActorDisplayName,
                OperationName: receipt.OperationName,
                Outcome: receipt.Outcome,
                ReasonCodes: receipt.ReasonCodes,
                CorrelationId: receipt.CorrelationId,
                DecisionStage: receipt.DecisionStage,
                PolicyVersion: receipt.PolicyVersion,
                PolicyHash: receipt.PolicyHash,
                CapabilityGrantId: receipt.CapabilityGrantId,
                Metadata: receipt.Metadata);
    }

    private PolicySimulationEndpointResult CreateEndpointResult(EndpointGovernanceResult result)
    {
        return result.AcknowledgmentChallenge is { } challenge
            ? new PolicySimulationEndpointResult(
                Kind: "acknowledgment-challenge",
                StatusCode: _endpointOptions.Value.AcknowledgmentChallengeStatusCode,
                Description: "The endpoint would answer with an acknowledgment challenge; the action would not run.",
                RequiredAcknowledgmentCode: challenge.RequiredAcknowledgmentCode,
                RequiredAcknowledgmentText: challenge.RequiredAcknowledgmentText)
            : result.CanExecute
            ? new PolicySimulationEndpointResult(
                Kind: "allowed",
                StatusCode: null,
                Description: "Governance would allow the request, and the action would run.",
                RequiredAcknowledgmentCode: null,
                RequiredAcknowledgmentText: null)
            : new PolicySimulationEndpointResult(
                Kind: "blocked",
                StatusCode: (result.FailureResult as IStatusCodeHttpResult)?.StatusCode
                    ?? StatusCodes.Status403Forbidden,
                Description: "Governance would block the request; the action would not run.",
                RequiredAcknowledgmentCode: null,
                RequiredAcknowledgmentText: null);
    }

    private static DefaultHttpContext CreateSimulatedRequest(
        HttpContext requestContext,
        IServiceProvider services,
        ClaimsPrincipal actor,
        Endpoint endpoint,
        PolicySimulatorOperation operation,
        Dictionary<string, StringValues> fields)
    {
        var simulated = new DefaultHttpContext
        {
            RequestServices = services,
            User = actor,
            TraceIdentifier = $"policy-simulator-{Guid.NewGuid():N}"
        };
        simulated.Request.Method = HttpMethods.Post;
        simulated.Request.Scheme = requestContext.Request.Scheme;
        simulated.Request.Host = requestContext.Request.Host;
        simulated.Request.Path = $"/api/v1/governed-operations/{operation.Key}";
        simulated.Request.ContentType = "application/x-www-form-urlencoded";
        simulated.Request.Form = new FormCollection(fields);
        simulated.SetEndpoint(endpoint);

        return simulated;
    }

    private async Task<EndpointGovernanceResult> RunOnSimulatedRequestAsync(
        HttpContext simulated,
        Func<Task<EndpointGovernanceResult>> evaluate)
    {
        // The governance services read the current request through IHttpContextAccessor, which flows with the
        // asynchronous execution context. Suppressing that flow gives the evaluation a context of its own, so setting
        // the accessor there never replaces, or clears, the real request's.
        Task<EndpointGovernanceResult> evaluation;
        using (ExecutionContext.SuppressFlow())
        {
            evaluation = Task.Run(async () =>
            {
                _httpContextAccessor.HttpContext = simulated;
                try
                {
                    return await evaluate().ConfigureAwait(false);
                }
                finally
                {
                    _httpContextAccessor.HttpContext = null;
                }
            });
        }

        return await evaluation.ConfigureAwait(false);
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

    private Endpoint? FindGovernedEndpoint(string actionName)
    {
        return _endpointDataSource.Endpoints.FirstOrDefault(endpoint =>
            endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action &&
            action.ControllerTypeInfo.AsType() == typeof(GovernedOperationsController) &&
            string.Equals(action.ActionName, actionName, StringComparison.Ordinal));
    }

    private sealed record PolicySimulatorOperation(
        string Key,
        string ActionName,
        string PayloadField,
        bool RequiresAcknowledgmentFlag);

    private sealed class SimulationRecorder(IReadOnlyList<string> constraintNames)
    {
        public IReadOnlyList<string> ConstraintNames { get; } = constraintNames;

        public ConstraintEvaluationResult?[] ConstraintResults { get; } = new ConstraintEvaluationResult?[constraintNames.Count];

        public GovernanceDecision? ComposedDecision { get; set; }

        public GovernanceDecision? PolicyDecision { get; set; }

        public GovernanceDecision? CapabilityInput { get; set; }

        public GovernanceDecision? CapabilityOutput { get; set; }

        public PolicySimulationTokenPreview? TokenPreview { get; set; }
    }

    private sealed class RecordingConstraint(
        IGovernanceConstraint<GovernanceEvaluationContext> inner,
        int index,
        SimulationRecorder recorder) : IGovernanceConstraint<GovernanceEvaluationContext>
    {
        public string Name => inner.Name;

        public async ValueTask<ConstraintEvaluationResult> EvaluateAsync(
            GovernanceEvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            ConstraintEvaluationResult result = await inner
                .EvaluateAsync(context, cancellationToken)
                .ConfigureAwait(false);
            recorder.ConstraintResults[index] = result;
            return result;
        }
    }

    private sealed class RecordingDecisionPolicy(
        IGovernanceDecisionPolicy<GovernanceEvaluationContext> inner,
        SimulationRecorder recorder) : IGovernanceDecisionPolicy<GovernanceEvaluationContext>
    {
        public async ValueTask<GovernanceDecision> ApplyAsync(
            GovernanceEvaluationContext context,
            GovernanceDecision composedDecision,
            IReadOnlyList<ConstraintEvaluationResult> constraintResults,
            CancellationToken cancellationToken = default)
        {
            recorder.ComposedDecision = composedDecision;
            GovernanceDecision decision = await inner
                .ApplyAsync(context, composedDecision, constraintResults, cancellationToken)
                .ConfigureAwait(false);
            recorder.PolicyDecision = decision;
            return decision;
        }
    }

    private sealed class RecordingCapabilityValidator(
        IEndpointCapabilityGrantValidator inner,
        SimulationRecorder recorder) : IEndpointCapabilityGrantValidator
    {
        public async ValueTask<GovernanceDecision> ValidateAsync(
            HttpContext httpContext,
            EndpointGovernanceDescriptor descriptor,
            GovernanceDecision currentDecision,
            CancellationToken cancellationToken = default)
        {
            recorder.CapabilityInput = currentDecision;
            GovernanceDecision decision = await inner
                .ValidateAsync(httpContext, descriptor, currentDecision, cancellationToken)
                .ConfigureAwait(false);
            recorder.CapabilityOutput = decision;
            return decision;
        }
    }

    private sealed class TokenPreviewCapabilityValidator(
        ClaimEndpointCapabilityGrantValidator entitlementValidator,
        IHttpGovernanceActorContextResolver actorContextResolver,
        CapabilityTokenIssuer issuer,
        CapabilityTokenSigningKey signingKey,
        Dictionary<string, StringValues> fields,
        SimulationRecorder recorder) : IEndpointCapabilityGrantValidator
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

    private sealed class CapturingDecisionReceiptSink : IDecisionReceiptSink
    {
        public IDecisionReceipt? Receipt { get; private set; }

        public ValueTask WriteAsync(IDecisionReceipt receipt, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(receipt);
            cancellationToken.ThrowIfCancellationRequested();
            Receipt = receipt;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SimulationServiceProvider(
        IServiceProvider inner,
        IGovernancePolicyEvaluator<GovernanceEvaluationContext> evaluator,
        IEndpointCapabilityGrantValidator capabilityValidator,
        IDecisionReceiptSink receiptSink) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            return serviceType == typeof(IGovernancePolicyEvaluator<GovernanceEvaluationContext>)
                ? evaluator
                : serviceType == typeof(IEndpointCapabilityGrantValidator)
                ? capabilityValidator
                : serviceType == typeof(IDecisionReceiptSink)
                ? receiptSink
                : inner.GetService(serviceType);
        }
    }
}
