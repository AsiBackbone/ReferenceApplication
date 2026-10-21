using System.Security.Claims;
using AsiBackbone.AspNetCore.Acknowledgments;
using AsiBackbone.AspNetCore.Actors;
using AsiBackbone.AspNetCore.Correlation;
using AsiBackbone.AspNetCore.Endpoints;
using AsiBackbone.AspNetCore.Results;
using AsiBackbone.Core.Audit;
using AsiBackbone.Core.Constraints;
using AsiBackbone.Core.Decisions;
using AsiBackbone.Core.Evaluation;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ReferenceApplication.Web.Controllers.Api;
using ReferenceApplication.Web.Extensions;

namespace ReferenceApplication.Web.Governance.Simulation;

/// <summary>
/// Describes one run of the endpoint governance pipeline against a simulated request.
/// </summary>
/// <param name="RequestContext">The request that asked for the run; its services back the simulated request.</param>
/// <param name="Endpoint">The real governed endpoint to evaluate.</param>
/// <param name="RequestPath">The path the simulated request uses.</param>
/// <param name="Actor">The simulated actor.</param>
/// <param name="Fields">The form fields the simulated request posts.</param>
/// <param name="Constraints">The constraints, in evaluation order, including any that are disabled.</param>
/// <param name="DisabledConstraints">The names of the constraints to leave out.</param>
/// <param name="DecisionPolicy">The decision policy applied to the composed decision.</param>
/// <param name="CreateCapabilityValidator">Creates the capability validator for the run, given its recorder.</param>
/// <param name="TraceIdentifierPrefix">The prefix of the simulated request's trace identifier.</param>
/// <param name="PolicyVersion">A policy version to evaluate under instead of the registered one.</param>
/// <param name="PolicyHash">The policy hash that goes with <paramref name="PolicyVersion" />.</param>
internal sealed record GovernancePipelineRequest(
    HttpContext RequestContext,
    Endpoint Endpoint,
    string RequestPath,
    ClaimsPrincipal Actor,
    Dictionary<string, StringValues> Fields,
    IReadOnlyList<IGovernanceConstraint<GovernanceEvaluationContext>> Constraints,
    IReadOnlySet<string> DisabledConstraints,
    IGovernanceDecisionPolicy<GovernanceEvaluationContext> DecisionPolicy,
    Func<GovernanceStageRecorder, IEndpointCapabilityGrantValidator> CreateCapabilityValidator,
    string TraceIdentifierPrefix,
    string? PolicyVersion = null,
    string? PolicyHash = null);

/// <summary>
/// The outcome of one pipeline run.
/// </summary>
/// <param name="Descriptor">The governance descriptor of the evaluated endpoint.</param>
/// <param name="Result">What endpoint governance decided to do with the request.</param>
/// <param name="Recorder">What each stage returned.</param>
/// <param name="Receipt">The decision receipt that would have been appended, when one was produced.</param>
internal sealed record GovernancePipelineRun(
    EndpointGovernanceDescriptor Descriptor,
    EndpointGovernanceResult Result,
    GovernanceStageRecorder Recorder,
    IDecisionReceipt? Receipt);

/// <summary>
/// Records what each governance stage returned during one pipeline run.
/// </summary>
/// <param name="constraintNames">The constraint names, in evaluation order.</param>
internal sealed class GovernanceStageRecorder(IReadOnlyList<string> constraintNames)
{
    /// <summary>Gets the constraint names, in evaluation order.</summary>
    public IReadOnlyList<string> ConstraintNames { get; } = constraintNames;

    /// <summary>Gets each constraint's result, or <see langword="null" /> when it did not run.</summary>
    public ConstraintEvaluationResult?[] ConstraintResults { get; } = new ConstraintEvaluationResult?[constraintNames.Count];

    /// <summary>Gets or sets the decision composed from the constraint results.</summary>
    public GovernanceDecision? ComposedDecision { get; set; }

    /// <summary>Gets or sets the decision after the decision policy.</summary>
    public GovernanceDecision? PolicyDecision { get; set; }

    /// <summary>Gets or sets the decision the capability validator received.</summary>
    public GovernanceDecision? CapabilityInput { get; set; }

    /// <summary>Gets or sets the decision the capability validator returned.</summary>
    public GovernanceDecision? CapabilityOutput { get; set; }

    /// <summary>Gets or sets the capability token preview, when the simulator previewed one.</summary>
    public PolicySimulationTokenPreview? TokenPreview { get; set; }
}

/// <summary>
/// Runs AsiBackbone's endpoint governance pipeline against a simulated request for a governed endpoint, and records
/// what each stage returns. The policy simulator and decision replay both evaluate through it.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here reimplements governance. A run builds a simulated request for the real governed endpoint and runs
/// AsiBackbone's <see cref="DefaultEndpointGovernanceService" />, the service the endpoint governance middleware runs,
/// with the policy evaluator composed through <see cref="AsiBackboneServiceExtensions.CreatePolicyEvaluator" />,
/// exactly as the registered evaluator is. Constraints, the decision policy, and the capability validator are wrapped
/// only to record what they return.
/// </para>
/// <para>
/// Nothing is persisted. The decision receipt sink is replaced with one that captures the receipt instead of writing
/// it. The simulated request runs on an isolated asynchronous flow, so services that read the current request through
/// <see cref="IHttpContextAccessor" /> see the simulated request and never the real one.
/// </para>
/// </remarks>
internal sealed class GovernancePipelineHarness(
    IHttpContextAccessor httpContextAccessor,
    IHttpGovernanceActorContextResolver actorContextResolver,
    IHttpGovernanceRequestCorrelationResolver correlationResolver,
    IAcknowledgmentChallengeService acknowledgmentChallengeService,
    IOptions<EndpointGovernanceOptions> endpointOptions,
    IOptions<GovernanceHttpResultMappingOptions> resultOptions,
    EndpointDataSource endpointDataSource,
    ILogger<DefaultGovernancePolicyEvaluator<GovernanceEvaluationContext>>? evaluatorLogger = null)
{
    private readonly IHttpContextAccessor _httpContextAccessor =
        httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
    private readonly IHttpGovernanceRequestCorrelationResolver _correlationResolver =
        correlationResolver ?? throw new ArgumentNullException(nameof(correlationResolver));
    private readonly IAcknowledgmentChallengeService _acknowledgmentChallengeService =
        acknowledgmentChallengeService ?? throw new ArgumentNullException(nameof(acknowledgmentChallengeService));
    private readonly IOptions<EndpointGovernanceOptions> _endpointOptions =
        endpointOptions ?? throw new ArgumentNullException(nameof(endpointOptions));
    private readonly IOptions<GovernanceHttpResultMappingOptions> _resultOptions =
        resultOptions ?? throw new ArgumentNullException(nameof(resultOptions));
    private readonly EndpointDataSource _endpointDataSource =
        endpointDataSource ?? throw new ArgumentNullException(nameof(endpointDataSource));
    private readonly ILogger<DefaultGovernancePolicyEvaluator<GovernanceEvaluationContext>>? _evaluatorLogger =
        evaluatorLogger;

    /// <summary>
    /// Gets AsiBackbone's actor resolver, which reads the simulated request during a run.
    /// </summary>
    public IHttpGovernanceActorContextResolver ActorContextResolver { get; } = actorContextResolver ?? throw new ArgumentNullException(nameof(actorContextResolver));

    /// <summary>
    /// Finds the endpoint of a <see cref="GovernedOperationsController" /> action.
    /// </summary>
    /// <param name="actionName">The action name.</param>
    /// <returns>The endpoint, or <see langword="null" /> when the action is not mapped.</returns>
    public Endpoint? FindGovernedEndpoint(string actionName)
    {
        return _endpointDataSource.Endpoints.FirstOrDefault(endpoint =>
            endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action &&
            action.ControllerTypeInfo.AsType() == typeof(GovernedOperationsController) &&
            string.Equals(action.ActionName, actionName, StringComparison.Ordinal));
    }

    /// <summary>
    /// Wraps a capability validator so the run records what it receives and returns.
    /// </summary>
    /// <param name="inner">The validator to wrap.</param>
    /// <param name="recorder">The run's recorder.</param>
    /// <returns>The recording validator.</returns>
    public static IEndpointCapabilityGrantValidator Record(
        IEndpointCapabilityGrantValidator inner,
        GovernanceStageRecorder recorder)
    {
        return new RecordingCapabilityValidator(inner, recorder);
    }

    /// <summary>
    /// Runs the endpoint governance pipeline against a simulated request. Nothing is persisted.
    /// </summary>
    /// <param name="request">What to run.</param>
    /// <param name="cancellationToken">A token that cancels the run.</param>
    /// <returns>The pipeline result and what each stage returned.</returns>
    public async Task<GovernancePipelineRun> RunAsync(
        GovernancePipelineRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var descriptor = EndpointGovernanceDescriptor.FromEndpoint(request.Endpoint);
        var recorder = new GovernanceStageRecorder([.. request.Constraints.Select(static constraint => constraint.Name)]);
        IGovernancePolicyEvaluator<GovernanceEvaluationContext> evaluator = AsiBackboneServiceExtensions.CreatePolicyEvaluator(
            request.Constraints
                .Select(static (constraint, index) => (constraint, index))
                .Where(entry => !request.DisabledConstraints.Contains(entry.constraint.Name))
                .Select(entry => new RecordingConstraint(entry.constraint, entry.index, recorder)),
            new RecordingDecisionPolicy(request.DecisionPolicy, recorder),
            _evaluatorLogger);
        var receiptSink = new CapturingDecisionReceiptSink();
        var services = new PipelineServiceProvider(
            request.RequestContext.RequestServices,
            evaluator,
            request.CreateCapabilityValidator(recorder),
            receiptSink);
        DefaultHttpContext simulated = CreateSimulatedRequest(request, services);
        IOptions<EndpointGovernanceOptions> governanceOptions = request.PolicyVersion is null
            ? _endpointOptions
            : new OptionsWrapper<EndpointGovernanceOptions>(
                WithPolicyIdentity(_endpointOptions.Value, request.PolicyVersion, request.PolicyHash));

        // A new instance, so the receipt sink it resolves is the capturing one; every other dependency is the
        // application's own.
        var governance = new DefaultEndpointGovernanceService(
            services,
            ActorContextResolver,
            _correlationResolver,
            _acknowledgmentChallengeService,
            governanceOptions,
            _resultOptions);

        EndpointGovernanceResult result = await RunOnSimulatedRequestAsync(
            simulated,
            () => governance.EvaluateAsync(simulated, descriptor, cancellationToken).AsTask())
            .ConfigureAwait(false);

        return new GovernancePipelineRun(descriptor, result, recorder, receiptSink.Receipt);
    }

    /// <summary>
    /// Reports each constraint's part in a run, in evaluation order.
    /// </summary>
    /// <param name="recorder">The run's recorder.</param>
    /// <param name="disabled">The names of the constraints that were left out.</param>
    /// <returns>The constraint stages.</returns>
    public static List<PolicySimulationConstraintStage> CreateConstraintStages(
        GovernanceStageRecorder recorder,
        IReadOnlySet<string> disabled)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(disabled);

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

    /// <summary>
    /// Reports the composed decision and the decision-policy step of a run.
    /// </summary>
    /// <param name="recorder">The run's recorder.</param>
    /// <returns>The decision-policy stage.</returns>
    public static PolicySimulationDecisionPolicyStage CreateDecisionPolicyStage(GovernanceStageRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);

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

    /// <summary>
    /// Reports the capability-grant step of a run.
    /// </summary>
    /// <param name="descriptor">The governance descriptor of the evaluated endpoint.</param>
    /// <param name="recorder">The run's recorder.</param>
    /// <param name="model">The grant model the run used.</param>
    /// <returns>The capability stage.</returns>
    public static PolicySimulationCapabilityStage CreateCapabilityStage(
        EndpointGovernanceDescriptor descriptor,
        GovernanceStageRecorder recorder,
        string model)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(recorder);

        return new PolicySimulationCapabilityStage(
            Applies: descriptor.CapabilityScopes.Count > 0,
            Model: model,
            Evaluated: recorder.CapabilityOutput is not null,
            RequiredScopes: descriptor.CapabilityScopes,
            InputOutcome: recorder.CapabilityInput?.Outcome.ToString(),
            Outcome: recorder.CapabilityOutput?.Outcome.ToString(),
            ReasonCodes: recorder.CapabilityOutput?.ReasonCodes ?? [],
            Token: recorder.TokenPreview);
    }

    /// <summary>
    /// Reports the decision receipt a run would have appended.
    /// </summary>
    /// <param name="receipt">The captured receipt.</param>
    /// <returns>The receipt, or <see langword="null" /> when none was produced.</returns>
    public static PolicySimulationReceipt? CreateReceipt(IDecisionReceipt? receipt)
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

    /// <summary>
    /// Reports what the real endpoint would do with a run's result.
    /// </summary>
    /// <param name="result">The pipeline result.</param>
    /// <returns>The endpoint result.</returns>
    public PolicySimulationEndpointResult CreateEndpointResult(EndpointGovernanceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

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

    private static DefaultHttpContext CreateSimulatedRequest(GovernancePipelineRequest request, IServiceProvider services)
    {
        var simulated = new DefaultHttpContext
        {
            RequestServices = services,
            User = request.Actor,
            TraceIdentifier = $"{request.TraceIdentifierPrefix}-{Guid.NewGuid():N}"
        };
        simulated.Request.Method = HttpMethods.Post;
        simulated.Request.Scheme = request.RequestContext.Request.Scheme;
        simulated.Request.Host = request.RequestContext.Request.Host;
        simulated.Request.Path = request.RequestPath;
        simulated.Request.ContentType = "application/x-www-form-urlencoded";
        simulated.Request.Form = new FormCollection(request.Fields);
        simulated.SetEndpoint(request.Endpoint);

        return simulated;
    }

    private static EndpointGovernanceOptions WithPolicyIdentity(
        EndpointGovernanceOptions source,
        string policyVersion,
        string? policyHash)
    {
        var options = new EndpointGovernanceOptions
        {
            PolicyVersion = policyVersion,
            PolicyHash = policyHash,
            MetadataMode = source.MetadataMode,
            FailClosedWhenPolicyEvaluatorMissing = source.FailClosedWhenPolicyEvaluatorMissing,
            FailClosedWhenCapabilityValidatorMissing = source.FailClosedWhenCapabilityValidatorMissing,
            FailClosedWhenAuditSinkMissing = source.FailClosedWhenAuditSinkMissing,
            ConfigurationFailureStatusCode = source.ConfigurationFailureStatusCode,
            CapabilityFailureStatusCode = source.CapabilityFailureStatusCode,
            AcknowledgmentChallengeStatusCode = source.AcknowledgmentChallengeStatusCode,
            RequireGovernanceMetadata = source.RequireGovernanceMetadata,
            EnableDevelopmentDiagnostics = source.EnableDevelopmentDiagnostics,
            DevelopmentDiagnosticsDocumentationBaseUrl = source.DevelopmentDiagnosticsDocumentationBaseUrl,
            IncludeDevelopmentDiagnosticsMetadataValues = source.IncludeDevelopmentDiagnosticsMetadataValues,
            DefaultForbiddenResultFactory = source.DefaultForbiddenResultFactory
        };

        foreach (string key in source.DevelopmentDiagnosticsRedactedMetadataKeys)
        {
            _ = options.DevelopmentDiagnosticsRedactedMetadataKeys.Add(key);
        }

        return options;
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

    private sealed class RecordingConstraint(
        IGovernanceConstraint<GovernanceEvaluationContext> inner,
        int index,
        GovernanceStageRecorder recorder) : IGovernanceConstraint<GovernanceEvaluationContext>
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
        GovernanceStageRecorder recorder) : IGovernanceDecisionPolicy<GovernanceEvaluationContext>
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
        GovernanceStageRecorder recorder) : IEndpointCapabilityGrantValidator
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

    private sealed class PipelineServiceProvider(
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
