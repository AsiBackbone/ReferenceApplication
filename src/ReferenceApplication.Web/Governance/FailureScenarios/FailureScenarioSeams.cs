using System.Security.Claims;
using AsiBackbone.AspNetCore.Actors;
using AsiBackbone.AspNetCore.Correlation;
using AsiBackbone.Core.Constraints;
using AsiBackbone.EntityFrameworkCore.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using ReferenceApplication.Infrastructure.Data.Entities;
using ReferenceApplication.Web.Authentication.Options;

namespace ReferenceApplication.Web.Governance.FailureScenarios;

/// <summary>
/// Marks a request that consumed an armed failure-scenario run.
/// </summary>
/// <param name="RunId">The run identifier.</param>
/// <param name="ScenarioId">The scenario the run belongs to.</param>
public sealed record FailureScenarioFeature(string RunId, string ScenarioId)
{
    /// <summary>
    /// Finds the scenario a request is running, when it is running one.
    /// </summary>
    /// <param name="httpContext">The request, or <see langword="null" /> outside a request.</param>
    /// <param name="scenarioId">The scenario to look for.</param>
    /// <returns>The feature, or <see langword="null" /> when the request is not running that scenario.</returns>
    public static FailureScenarioFeature? For(HttpContext? httpContext, string scenarioId)
    {
        return httpContext?.Features.Get<FailureScenarioFeature>() is { } feature &&
            string.Equals(feature.ScenarioId, scenarioId, StringComparison.Ordinal)
            ? feature
            : null;
    }
}

/// <summary>
/// Consumes the armed failure-scenario run a request names, and records how the request ended.
/// </summary>
/// <remarks>
/// <para>
/// Registered only while the failure scenarios are active, between authorization and endpoint governance. A request
/// without the run header passes through untouched. A request with the header must consume an unexpired run armed for
/// the same actor, or it is refused with 409 before governance runs, so a used, expired, or foreign run identifier never
/// reaches an endpoint.
/// </para>
/// <para>
/// For the missing-capability-grant scenario, the request's principal is replaced with a copy that lacks the
/// consequential capability claim, so the actor's own grant cannot satisfy the check for this one request.
/// </para>
/// </remarks>
/// <param name="next">The next middleware.</param>
public sealed class FailureScenarioMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    /// <summary>
    /// Processes a request.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="registry">The run registry.</param>
    /// <param name="actorContextResolver">AsiBackbone's actor resolver.</param>
    /// <param name="correlationResolver">AsiBackbone's request correlation resolver.</param>
    /// <param name="authorizationOptions">The application's claim mapping.</param>
    /// <returns>A task that completes when the request has been processed.</returns>
    public async Task InvokeAsync(
        HttpContext httpContext,
        FailureScenarioRegistry registry,
        IHttpGovernanceActorContextResolver actorContextResolver,
        IHttpGovernanceRequestCorrelationResolver correlationResolver,
        IOptions<ApplicationAuthorizationOptions> authorizationOptions)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(actorContextResolver);
        ArgumentNullException.ThrowIfNull(correlationResolver);
        ArgumentNullException.ThrowIfNull(authorizationOptions);

        string? runId = httpContext.Request.Headers[FailureScenarioOptions.RunHeaderName].FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(runId))
        {
            await _next(httpContext).ConfigureAwait(false);
            return;
        }

        // Governance resolves the same correlation identifier for the request, so the run can find its evidence.
        string correlationId = correlationResolver.ResolveRequestCorrelation().CorrelationId ?? httpContext.TraceIdentifier;
        string actorId = actorContextResolver.ResolveActorContext().ActorId;
        FailureScenarioRunState? run = runId.Length <= FailureScenarioOptions.MaximumRunIdLength
            ? registry.TryBegin(runId, actorId, correlationId)
            : null;

        if (run is null)
        {
            await Results.Problem(
                    title: "The failure scenario is not armed.",
                    detail: "The run is unknown, has expired, belongs to another actor, or was already used. Arm the scenario again.",
                    statusCode: StatusCodes.Status409Conflict)
                .ExecuteAsync(httpContext)
                .ConfigureAwait(false);
            return;
        }

        httpContext.Features.Set(new FailureScenarioFeature(run.RunId, run.ScenarioId));
        if (string.Equals(run.ScenarioId, FailureScenarioCatalog.MissingCapabilityGrant, StringComparison.Ordinal))
        {
            httpContext.User = WithoutCapability(httpContext.User, authorizationOptions.Value.PermissionClaimType);
            registry.RecordFault(run.RunId, FailureScenarioFaultPoints.CapabilityClaimWithheld);
        }

        try
        {
            await _next(httpContext).ConfigureAwait(false);
            registry.Complete(run.RunId, httpContext.Response.StatusCode);
        }
        catch (GovernanceEvidencePersistenceException exception)
        {
            registry.Complete(run.RunId, StatusCodes.Status500InternalServerError, exception.GetType().Name, exception.ReasonCode);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            registry.Complete(run.RunId, StatusCodes.Status500InternalServerError, exception.GetType().Name);
            throw;
        }
    }

    private static ClaimsPrincipal WithoutCapability(ClaimsPrincipal principal, string permissionClaimType)
    {
        return new ClaimsPrincipal(principal.Identities.Select(identity =>
        {
            ClaimsIdentity copy = identity.Clone();
            foreach (Claim claim in copy.FindAll(claim =>
                string.Equals(claim.Type, permissionClaimType, StringComparison.Ordinal) &&
                string.Equals(claim.Value, ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope, StringComparison.Ordinal)).ToList())
            {
                copy.RemoveClaim(claim);
            }

            return copy;
        }));
    }
}

/// <summary>
/// A Development-only demonstration constraint that throws during policy evaluation when its scenario is armed for the
/// request, and otherwise does not apply.
/// </summary>
/// <remarks>
/// It is registered only by <c>AddApplicationFailureScenarios</c>, which registers nothing outside Development.
/// </remarks>
/// <param name="httpContextAccessor">The current request accessor.</param>
/// <param name="registry">The run registry.</param>
public sealed class FailureScenarioThrowingConstraint(
    IHttpContextAccessor httpContextAccessor,
    FailureScenarioRegistry registry) : IGovernanceConstraint<GovernanceEvaluationContext>
{
    /// <summary>
    /// Gets the stable name of this constraint.
    /// </summary>
    public const string ConstraintName = "referenceapplication.failure_scenarios.throwing_constraint";

    private readonly IHttpContextAccessor _httpContextAccessor =
        httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
    private readonly FailureScenarioRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <inheritdoc />
    public string Name => ConstraintName;

    /// <inheritdoc />
    public ValueTask<ConstraintEvaluationResult> EvaluateAsync(
        GovernanceEvaluationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (FailureScenarioFeature.For(_httpContextAccessor.HttpContext, FailureScenarioCatalog.ConstraintThrows) is not { } feature)
        {
            return ValueTask.FromResult(ConstraintEvaluationResult.NotApplicable());
        }

        _registry.RecordFault(feature.RunId, FailureScenarioFaultPoints.ConstraintThrew);
        throw new InvalidOperationException("The failure-scenario demonstration constraint threw, as its scenario was armed.");
    }
}

/// <summary>
/// Fails a database save, as a provider failure would, for the write an armed scenario targets.
/// </summary>
/// <remarks>
/// <para>
/// The fault is injected at the persistence layer, so the code under demonstration handles a real
/// <see cref="DbUpdateException" />: AsiBackbone's ledger store and the decision-receipt sink for a failed ledger
/// append, NCAT's audited transaction for a failed mutation, and the audit-completion publisher for a failed lifecycle
/// append.
/// </para>
/// <list type="bullet">
/// <item><description>Ledger append fails: a save that adds a ledger record during the armed request.</description></item>
/// <item><description>Execution fails after approval: a save that adds a governed record during the armed request.</description></item>
/// <item><description>Outbox delivery fails: a save that adds the lifecycle event for the armed request's correlation,
/// until the run's delivery fault is cleared. Delivery runs on the outbox worker, outside any request.</description></item>
/// </list>
/// </remarks>
/// <param name="httpContextAccessor">The current request accessor.</param>
/// <param name="registry">The run registry.</param>
public sealed class FailureScenarioSaveChangesInterceptor(
    IHttpContextAccessor httpContextAccessor,
    FailureScenarioRegistry registry) : SaveChangesInterceptor
{
    private readonly IHttpContextAccessor _httpContextAccessor =
        httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
    private readonly FailureScenarioRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ThrowIfArmed(eventData);
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfArmed(eventData);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void ThrowIfArmed(DbContextEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is not { } context)
        {
            return;
        }

        HttpContext? httpContext = _httpContextAccessor.HttpContext;
        List<object> added = [.. context.ChangeTracker.Entries()
            .Where(static entry => entry.State == EntityState.Added)
            .Select(static entry => entry.Entity)];

        if (FailureScenarioFeature.For(httpContext, FailureScenarioCatalog.LedgerAppendFails) is { } ledgerRun &&
            added.OfType<AuditLedgerRecordEntity>().Any())
        {
            Fail(ledgerRun.RunId, FailureScenarioFaultPoints.LedgerAppend, "appending the decision receipt");
        }

        if (FailureScenarioFeature.For(httpContext, FailureScenarioCatalog.ExecutionFailsAfterApproval) is { } mutationRun &&
            added.OfType<GovernedOperationRecord>().Any())
        {
            Fail(mutationRun.RunId, FailureScenarioFaultPoints.MutationSave, "saving the governed record");
        }

        foreach (DecisionReceiptLifecycleEventEntity lifecycleEvent in added.OfType<DecisionReceiptLifecycleEventEntity>())
        {
            if (_registry.FindDeliveryFault(lifecycleEvent.CorrelationId) is { } deliveryRunId)
            {
                Fail(deliveryRunId, FailureScenarioFaultPoints.LifecycleAppend, "appending the lifecycle event");
            }
        }
    }

    private void Fail(string runId, string faultPoint, string operation)
    {
        _registry.RecordFault(runId, faultPoint);
        throw new DbUpdateException($"The failure scenario failed the database save {operation}, as it was armed to.");
    }
}
