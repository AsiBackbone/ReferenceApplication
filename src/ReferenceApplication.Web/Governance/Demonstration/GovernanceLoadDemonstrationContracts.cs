using System.Threading.Channels;

namespace ReferenceApplication.Web.Governance.Demonstration;

/// <summary>
/// Identifies the lifecycle state of a governance load demonstration run.
/// </summary>
public enum GovernanceLoadDemonstrationState
{
    /// <summary>No run has started in this application process.</summary>
    Idle,

    /// <summary>A bounded run is active.</summary>
    Running,

    /// <summary>The requested iteration count completed within the duration limit.</summary>
    Completed,

    /// <summary>An administrator cancelled the run.</summary>
    Cancelled,

    /// <summary>The duration limit stopped the run before the requested iteration count completed.</summary>
    DurationLimitReached,

    /// <summary>The run stopped because evaluation failed.</summary>
    Failed
}

/// <summary>
/// Represents one bucket in the transient decision-latency histogram.
/// </summary>
/// <param name="Label">A human-readable upper-bound label.</param>
/// <param name="Count">The number of decisions observed in the bucket.</param>
public sealed record GovernanceLoadLatencyBucket(string Label, long Count);

/// <summary>
/// Describes the current aggregate state of one bounded demonstration run.
/// </summary>
/// <param name="RunId">The process-local run identifier.</param>
/// <param name="State">The run lifecycle state.</param>
/// <param name="CompletedIterations">The number of completed evaluations.</param>
/// <param name="TargetIterations">The requested bounded evaluation count.</param>
/// <param name="ElapsedMilliseconds">The elapsed wall-clock time.</param>
/// <param name="ThroughputPerSecond">The aggregate decisions completed per second.</param>
/// <param name="LatencyHistogram">The aggregate per-decision latency histogram.</param>
/// <param name="Message">A short status description.</param>
public sealed record GovernanceLoadDemonstrationSnapshot(
    string? RunId,
    GovernanceLoadDemonstrationState State,
    int CompletedIterations,
    int TargetIterations,
    double ElapsedMilliseconds,
    double ThroughputPerSecond,
    IReadOnlyList<GovernanceLoadLatencyBucket> LatencyHistogram,
    string Message);

/// <summary>
/// Identifies the result of attempting to start a bounded demonstration run.
/// </summary>
public enum GovernanceLoadDemonstrationStartStatus
{
    /// <summary>The run was accepted.</summary>
    Started,

    /// <summary>Another run is already active.</summary>
    AlreadyRunning,

    /// <summary>The requested bounds are invalid.</summary>
    InvalidBounds
}

/// <summary>
/// Returns the result and current state after a start attempt.
/// </summary>
/// <param name="Status">The start result.</param>
/// <param name="Snapshot">The current demonstration state.</param>
public sealed record GovernanceLoadDemonstrationStartResult(
    GovernanceLoadDemonstrationStartStatus Status,
    GovernanceLoadDemonstrationSnapshot Snapshot);

/// <summary>
/// Starts, cancels, and observes the process-local governance load demonstration.
/// </summary>
public interface IGovernanceLoadDemonstrationService
{
    /// <summary>Gets the current aggregate state.</summary>
    GovernanceLoadDemonstrationSnapshot Current { get; }

    /// <summary>Attempts to start a run with explicit iteration and duration bounds.</summary>
    GovernanceLoadDemonstrationStartResult TryStart(int iterations, int durationMilliseconds);

    /// <summary>Requests cancellation of the active run.</summary>
    bool TryCancel();

    /// <summary>Subscribes to current and future aggregate state updates.</summary>
    IGovernanceLoadDemonstrationSubscription Subscribe();
}

/// <summary>
/// Represents one bounded live-update subscription.
/// </summary>
public interface IGovernanceLoadDemonstrationSubscription : IAsyncDisposable
{
    /// <summary>Reads aggregate updates until cancellation or disposal.</summary>
    IAsyncEnumerable<GovernanceLoadDemonstrationSnapshot> ReadAllAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes one decision through the production governance evaluator composition.
/// </summary>
internal interface IGovernanceLoadDemonstrationExecutor
{
    ValueTask EvaluateAsync(string correlationId, CancellationToken cancellationToken);
}

internal sealed class GovernanceLoadDemonstrationSubscription(
    Action<Guid> remove,
    Guid subscriptionId,
    Channel<GovernanceLoadDemonstrationSnapshot> channel) : IGovernanceLoadDemonstrationSubscription
{
    private int _disposed;

    public IAsyncEnumerable<GovernanceLoadDemonstrationSnapshot> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        return channel.Reader.ReadAllAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            remove(subscriptionId);
            channel.Writer.TryComplete();
        }

        return ValueTask.CompletedTask;
    }
}
