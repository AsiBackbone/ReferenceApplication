namespace ReferenceApplication.Web.Governance.FailureScenarios;

/// <summary>
/// Holds the armed and completed failure-scenario runs of this process, and the delivery faults they hold.
/// </summary>
/// <remarks>
/// <para>
/// A run is armed for one actor and one request. <see cref="TryBegin" /> consumes it, so the request that carries its
/// identifier is the only one a fault-injection seam can act on, and a second request with the same identifier is
/// refused. An armed run expires after <see cref="FailureScenarioOptions.ArmLifetime" />.
/// </para>
/// <para>
/// The registry is in memory and bounded to <see cref="FailureScenarioOptions.RetainedRunCount" /> runs. Nothing about
/// a run is persisted: its evidence is whatever the governed request left in the demonstration database.
/// </para>
/// </remarks>
/// <param name="timeProvider">The clock that arms and expires runs.</param>
public sealed class FailureScenarioRegistry(TimeProvider timeProvider)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly Lock _gate = new();
    private readonly LinkedList<Run> _runs = new();

    /// <summary>
    /// Arms a run for one request by one actor.
    /// </summary>
    /// <param name="scenarioId">The scenario the run belongs to.</param>
    /// <param name="actorId">The governance actor who may send the request.</param>
    /// <param name="request">The request to send, without the run header, which this method adds.</param>
    /// <returns>The armed run.</returns>
    public FailureScenarioRunState Arm(string scenarioId, string actorId, FailureScenarioRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(request);

        string runId = Guid.NewGuid().ToString("N");
        DateTimeOffset now = _timeProvider.GetUtcNow();
        var run = new Run(
            runId,
            scenarioId,
            actorId,
            now,
            now + FailureScenarioOptions.ArmLifetime,
            request with
            {
                Headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase)
                {
                    [FailureScenarioOptions.RunHeaderName] = runId
                }
            });

        lock (_gate)
        {
            _ = _runs.AddLast(run);
            while (_runs.Count > FailureScenarioOptions.RetainedRunCount)
            {
                _runs.RemoveFirst();
            }

            return run.ToState(now);
        }
    }

    /// <summary>
    /// Consumes an armed run for the request that carries its identifier.
    /// </summary>
    /// <param name="runId">The run identifier from the request header.</param>
    /// <param name="actorId">The governance actor sending the request.</param>
    /// <param name="correlationId">The request's governance correlation identifier.</param>
    /// <returns>The run, or <see langword="null" /> when no unexpired run is armed under that identifier for the actor.</returns>
    public FailureScenarioRunState? TryBegin(string runId, string actorId, string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            Run? run = FindRun(runId);
            if (run is null ||
                run.Status != FailureScenarioRunStatuses.Armed ||
                now > run.ExpiresUtc ||
                !string.Equals(run.ActorId, actorId, StringComparison.Ordinal))
            {
                return null;
            }

            run.Status = FailureScenarioRunStatuses.Running;
            run.CorrelationId = correlationId;
            run.StartedUtc = now;
            run.DeliveryFaultActive = string.Equals(
                run.ScenarioId,
                FailureScenarioCatalog.OutboxDeliveryFails,
                StringComparison.Ordinal);
            return run.ToState(now);
        }
    }

    /// <summary>
    /// Records that a fault-injection seam acted for a run.
    /// </summary>
    /// <param name="runId">The run identifier.</param>
    /// <param name="faultPoint">Where the fault was injected.</param>
    public void RecordFault(string runId, string faultPoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(faultPoint);

        lock (_gate)
        {
            if (FindRun(runId) is { } run)
            {
                run.Faults.Add(new FailureScenarioFault(faultPoint, _timeProvider.GetUtcNow()));
            }
        }
    }

    /// <summary>
    /// Records how the run's request ended.
    /// </summary>
    /// <param name="runId">The run identifier.</param>
    /// <param name="statusCode">The response status code.</param>
    /// <param name="exceptionType">The type of the exception that ended the request, when one did.</param>
    /// <param name="reasonCode">The sanitized reason code the exception carried, when it carried one.</param>
    public void Complete(string runId, int statusCode, string? exceptionType = null, string? reasonCode = null)
    {
        lock (_gate)
        {
            if (FindRun(runId) is { } run)
            {
                run.Status = FailureScenarioRunStatuses.Completed;
                run.CompletedUtc = _timeProvider.GetUtcNow();
                run.StatusCode = statusCode;
                run.ExceptionType = exceptionType;
                run.ExceptionReasonCode = reasonCode;
            }
        }
    }

    /// <summary>
    /// Finds a run.
    /// </summary>
    /// <param name="runId">The run identifier.</param>
    /// <returns>The run, or <see langword="null" /> when it is unknown or was forgotten.</returns>
    public FailureScenarioRunState? Find(string runId)
    {
        lock (_gate)
        {
            return FindRun(runId)?.ToState(_timeProvider.GetUtcNow());
        }
    }

    /// <summary>
    /// Finds the run whose delivery fault applies to an audit completion.
    /// </summary>
    /// <param name="correlationId">The correlation identifier of the completion being delivered.</param>
    /// <returns>The run identifier, or <see langword="null" /> when no active delivery fault applies.</returns>
    public string? FindDeliveryFault(string? correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            return null;
        }

        lock (_gate)
        {
            return _runs.FirstOrDefault(run =>
                run.DeliveryFaultActive &&
                string.Equals(run.CorrelationId, correlationId, StringComparison.Ordinal))?.RunId;
        }
    }

    /// <summary>
    /// Clears a run's delivery fault, so the next delivery attempt can succeed.
    /// </summary>
    /// <param name="runId">The run identifier.</param>
    /// <returns><see langword="true" /> when the run held an active delivery fault.</returns>
    public bool ClearDeliveryFault(string runId)
    {
        lock (_gate)
        {
            if (FindRun(runId) is not { DeliveryFaultActive: true } run)
            {
                return false;
            }

            run.DeliveryFaultActive = false;
            run.DeliveryFaultClearedUtc = _timeProvider.GetUtcNow();
            return true;
        }
    }

    private Run? FindRun(string runId)
    {
        return _runs.FirstOrDefault(run => string.Equals(run.RunId, runId, StringComparison.Ordinal));
    }

    private sealed class Run(
        string runId,
        string scenarioId,
        string actorId,
        DateTimeOffset armedUtc,
        DateTimeOffset expiresUtc,
        FailureScenarioRequest request)
    {
        public string RunId { get; } = runId;

        public string ScenarioId { get; } = scenarioId;

        public string ActorId { get; } = actorId;

        public DateTimeOffset ArmedUtc { get; } = armedUtc;

        public DateTimeOffset ExpiresUtc { get; } = expiresUtc;

        public FailureScenarioRequest Request { get; } = request;

        public string Status { get; set; } = FailureScenarioRunStatuses.Armed;

        public string? CorrelationId { get; set; }

        public DateTimeOffset? StartedUtc { get; set; }

        public DateTimeOffset? CompletedUtc { get; set; }

        public int? StatusCode { get; set; }

        public string? ExceptionType { get; set; }

        public string? ExceptionReasonCode { get; set; }

        public bool DeliveryFaultActive { get; set; }

        public DateTimeOffset? DeliveryFaultClearedUtc { get; set; }

        public List<FailureScenarioFault> Faults { get; } = [];

        public FailureScenarioRunState ToState(DateTimeOffset now)
        {
            bool expired = Status == FailureScenarioRunStatuses.Armed && now > ExpiresUtc;
            return new FailureScenarioRunState(
                RunId,
                ScenarioId,
                ActorId,
                expired ? FailureScenarioRunStatuses.Expired : Status,
                ArmedUtc,
                ExpiresUtc,
                Status == FailureScenarioRunStatuses.Armed && !expired ? Request : null,
                CorrelationId,
                StartedUtc,
                CompletedUtc,
                StatusCode,
                ExceptionType,
                ExceptionReasonCode,
                [.. Faults],
                DeliveryFaultActive,
                DeliveryFaultClearedUtc);
        }
    }
}

/// <summary>
/// Names the states of a failure-scenario run.
/// </summary>
public static class FailureScenarioRunStatuses
{
    /// <summary>The run waits for its request.</summary>
    public const string Armed = "armed";

    /// <summary>The run's request is in progress.</summary>
    public const string Running = "running";

    /// <summary>The run's request has ended.</summary>
    public const string Completed = "completed";

    /// <summary>The run expired before a request used it.</summary>
    public const string Expired = "expired";
}

/// <summary>
/// Names where a fault-injection seam can act.
/// </summary>
public static class FailureScenarioFaultPoints
{
    /// <summary>The capability claim was withheld from the request.</summary>
    public const string CapabilityClaimWithheld = "capability-claim-withheld";

    /// <summary>The demonstration constraint threw during policy evaluation.</summary>
    public const string ConstraintThrew = "constraint-threw";

    /// <summary>The save that appends the decision receipt to the ledger failed.</summary>
    public const string LedgerAppend = "ledger-append";

    /// <summary>The save that commits the governed mutation failed.</summary>
    public const string MutationSave = "mutation-save";

    /// <summary>The save that appends the lifecycle event for a delivered completion failed.</summary>
    public const string LifecycleAppend = "lifecycle-append";
}

/// <summary>
/// The request a failure-scenario run expects. The caller adds the antiforgery token.
/// </summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Path">The governed endpoint path.</param>
/// <param name="Fields">The form fields to post, besides the antiforgery token.</param>
/// <param name="Headers">The headers to send, including the run header.</param>
public sealed record FailureScenarioRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// A fault a seam injected for a run.
/// </summary>
/// <param name="Point">Where the fault was injected; one of <see cref="FailureScenarioFaultPoints" />.</param>
/// <param name="OccurredUtc">When it was injected.</param>
public sealed record FailureScenarioFault(string Point, DateTimeOffset OccurredUtc);

/// <summary>
/// The state of a failure-scenario run.
/// </summary>
/// <param name="RunId">The run identifier.</param>
/// <param name="ScenarioId">The scenario the run belongs to.</param>
/// <param name="ActorId">The governance actor who armed the run.</param>
/// <param name="Status">One of <see cref="FailureScenarioRunStatuses" />.</param>
/// <param name="ArmedUtc">When the run was armed.</param>
/// <param name="ExpiresUtc">When an unused run expires.</param>
/// <param name="Request">The request to send, while the run is armed.</param>
/// <param name="CorrelationId">The governance correlation identifier of the run's request.</param>
/// <param name="StartedUtc">When the run's request began.</param>
/// <param name="CompletedUtc">When the run's request ended.</param>
/// <param name="StatusCode">The response status code.</param>
/// <param name="ExceptionType">The type of the exception that ended the request, when one did.</param>
/// <param name="ExceptionReasonCode">The reason code that exception carried.</param>
/// <param name="Faults">The faults the seams injected, in order.</param>
/// <param name="DeliveryFaultActive">Whether delivery of the request's audit completion still fails.</param>
/// <param name="DeliveryFaultClearedUtc">When the delivery fault was cleared.</param>
public sealed record FailureScenarioRunState(
    string RunId,
    string ScenarioId,
    string ActorId,
    string Status,
    DateTimeOffset ArmedUtc,
    DateTimeOffset ExpiresUtc,
    FailureScenarioRequest? Request,
    string? CorrelationId,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    int? StatusCode,
    string? ExceptionType,
    string? ExceptionReasonCode,
    IReadOnlyList<FailureScenarioFault> Faults,
    bool DeliveryFaultActive,
    DateTimeOffset? DeliveryFaultClearedUtc);
