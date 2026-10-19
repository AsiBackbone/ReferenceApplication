using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace ReferenceApplication.Web.Governance.Demonstration;

/// <summary>
/// Runs one bounded in-process governance demonstration at a time and publishes transient aggregate metrics.
/// </summary>
internal sealed partial class GovernanceLoadDemonstrationService(
    IServiceScopeFactory scopeFactory,
    IOptions<GovernanceLoadDemonstrationOptions> optionsAccessor,
    IHostApplicationLifetime applicationLifetime,
    ILogger<GovernanceLoadDemonstrationService> logger) :
    IGovernanceLoadDemonstrationService,
    IDisposable
{
    private const int _subscriberCapacity = 16;
    private static readonly double[] _latencyUpperBoundsMilliseconds = [0.01, 0.05, 0.1, 0.5, 1, 5];
    private static readonly string[] _latencyLabels = ["≤ 0.01 ms", "≤ 0.05 ms", "≤ 0.1 ms", "≤ 0.5 ms", "≤ 1 ms", "≤ 5 ms", "> 5 ms"];
    private readonly Lock _gate = new();
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly GovernanceLoadDemonstrationOptions _options = optionsAccessor?.Value
        ?? throw new ArgumentNullException(nameof(optionsAccessor));
    private readonly CancellationToken _applicationStopping = applicationLifetime?.ApplicationStopping
        ?? throw new ArgumentNullException(nameof(applicationLifetime));
    private readonly ILogger<GovernanceLoadDemonstrationService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));
    private readonly ConcurrentDictionary<Guid, Channel<GovernanceLoadDemonstrationSnapshot>> _subscribers = new();
    private GovernanceLoadDemonstrationSnapshot _current = CreateIdleSnapshot();
    private RunControl? _activeRun;
    private bool _disposed;

    public GovernanceLoadDemonstrationSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public GovernanceLoadDemonstrationStartResult TryStart(int iterations, int durationMilliseconds)
    {
        if (iterations < 1 || iterations > _options.MaximumIterations ||
            durationMilliseconds < 1 || durationMilliseconds > _options.MaximumDurationMilliseconds)
        {
            return new GovernanceLoadDemonstrationStartResult(
                GovernanceLoadDemonstrationStartStatus.InvalidBounds,
                Current);
        }

        GovernanceLoadDemonstrationSnapshot started;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeRun is not null)
            {
                return new GovernanceLoadDemonstrationStartResult(
                    GovernanceLoadDemonstrationStartStatus.AlreadyRunning,
                    _current);
            }

            string runId = Guid.NewGuid().ToString("N");
            var control = new RunControl(runId);
            _activeRun = control;
            started = new GovernanceLoadDemonstrationSnapshot(
                runId,
                GovernanceLoadDemonstrationState.Running,
                CompletedIterations: 0,
                TargetIterations: iterations,
                ElapsedMilliseconds: 0,
                ThroughputPerSecond: 0,
                CreateHistogram(new long[_latencyLabels.Length]),
                "Bounded governance demonstration started.");
            _current = started;
            Publish(started);
            control.Execution = Task.Run(
                () => RunAsync(control, iterations, durationMilliseconds),
                CancellationToken.None);
        }

        return new GovernanceLoadDemonstrationStartResult(
            GovernanceLoadDemonstrationStartStatus.Started,
            started);
    }

    public bool TryCancel()
    {
        lock (_gate)
        {
            if (_activeRun is null)
            {
                return false;
            }

            Interlocked.Exchange(ref _activeRun.CancellationRequested, 1);
            _activeRun.Cancellation.Cancel();
            return true;
        }
    }

    public IGovernanceLoadDemonstrationSubscription Subscribe()
    {
        var channel = Channel.CreateBounded<GovernanceLoadDemonstrationSnapshot>(new BoundedChannelOptions(_subscriberCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        var subscriptionId = Guid.NewGuid();
        if (!_subscribers.TryAdd(subscriptionId, channel))
        {
            throw new InvalidOperationException("The load demonstration subscription could not be registered.");
        }

        _ = channel.Writer.TryWrite(Current);
        return new GovernanceLoadDemonstrationSubscription(RemoveSubscription, subscriptionId, channel);
    }

    public void Dispose()
    {
        RunControl? activeRun;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            activeRun = _activeRun;
        }

        activeRun?.Cancellation.Cancel();
        foreach (Channel<GovernanceLoadDemonstrationSnapshot> channel in _subscribers.Values)
        {
            channel.Writer.TryComplete();
        }

    }

    private async Task RunAsync(RunControl control, int targetIterations, int durationMilliseconds)
    {
        long[] counts = new long[_latencyLabels.Length];
        long runStarted = Stopwatch.GetTimestamp();
        long lastPublished = runStarted;
        int completed = 0;
        GovernanceLoadDemonstrationState finalState = GovernanceLoadDemonstrationState.Completed;
        string finalMessage = "Requested bounded governance decisions completed.";

        using var durationCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(durationMilliseconds));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            control.Cancellation.Token,
            durationCancellation.Token,
            _applicationStopping);

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            IGovernanceLoadDemonstrationExecutor executor = scope.ServiceProvider
                .GetRequiredService<IGovernanceLoadDemonstrationExecutor>();

            for (; completed < targetIterations; completed++)
            {
                linkedCancellation.Token.ThrowIfCancellationRequested();
                long decisionStarted = Stopwatch.GetTimestamp();
                await executor.EvaluateAsync(control.RunId, linkedCancellation.Token).ConfigureAwait(false);
                AddLatency(counts, Stopwatch.GetElapsedTime(decisionStarted).TotalMilliseconds);

                long now = Stopwatch.GetTimestamp();
                if (Stopwatch.GetElapsedTime(lastPublished, now).TotalMilliseconds >= _options.ProgressIntervalMilliseconds)
                {
                    Publish(CreateSnapshot(
                        control.RunId,
                        GovernanceLoadDemonstrationState.Running,
                        completed + 1,
                        targetIterations,
                        runStarted,
                        now,
                        counts,
                        "Evaluating bounded in-process governance decisions."));
                    lastPublished = now;
                }

                if ((completed & 127) == 127)
                {
                    await Task.Yield();
                }
            }
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            if (Volatile.Read(ref control.CancellationRequested) == 1)
            {
                finalState = GovernanceLoadDemonstrationState.Cancelled;
                finalMessage = "The administrator cancelled the demonstration.";
            }
            else if (durationCancellation.IsCancellationRequested)
            {
                finalState = GovernanceLoadDemonstrationState.DurationLimitReached;
                finalMessage = "The hard duration limit stopped the demonstration.";
            }
            else
            {
                finalState = GovernanceLoadDemonstrationState.Cancelled;
                finalMessage = "Application shutdown cancelled the demonstration.";
            }
        }
        catch (Exception exception)
        {
            finalState = GovernanceLoadDemonstrationState.Failed;
            finalMessage = "The demonstration stopped after an evaluation failure.";
            LogDemonstrationFailure(_logger, control.RunId, exception);
        }
        finally
        {
            long ended = Stopwatch.GetTimestamp();
            GovernanceLoadDemonstrationSnapshot completedSnapshot = CreateSnapshot(
                control.RunId,
                finalState,
                completed,
                targetIterations,
                runStarted,
                ended,
                counts,
                finalMessage);
            lock (_gate)
            {
                _current = completedSnapshot;
                Publish(completedSnapshot);
                if (ReferenceEquals(_activeRun, control))
                {
                    _activeRun = null;
                }
            }
            control.Cancellation.Dispose();
        }
    }

    private static GovernanceLoadDemonstrationSnapshot CreateSnapshot(
        string runId,
        GovernanceLoadDemonstrationState state,
        int completedIterations,
        int targetIterations,
        long started,
        long ended,
        long[] counts,
        string message)
    {
        double elapsedMilliseconds = Stopwatch.GetElapsedTime(started, ended).TotalMilliseconds;
        double throughput = elapsedMilliseconds <= 0
            ? 0
            : completedIterations / TimeSpan.FromMilliseconds(elapsedMilliseconds).TotalSeconds;
        return new GovernanceLoadDemonstrationSnapshot(
            runId,
            state,
            completedIterations,
            targetIterations,
            elapsedMilliseconds,
            throughput,
            CreateHistogram(counts),
            message);
    }

    private static GovernanceLoadDemonstrationSnapshot CreateIdleSnapshot()
    {
        return new GovernanceLoadDemonstrationSnapshot(
            RunId: null,
            GovernanceLoadDemonstrationState.Idle,
            CompletedIterations: 0,
            TargetIterations: 0,
            ElapsedMilliseconds: 0,
            ThroughputPerSecond: 0,
            CreateHistogram(new long[_latencyLabels.Length]),
            "No demonstration has run in this application process.");
    }

    private static GovernanceLoadLatencyBucket[] CreateHistogram(long[] counts)
    {
        return [.. _latencyLabels.Select((label, index) => new GovernanceLoadLatencyBucket(label, counts[index]))];
    }

    private static void AddLatency(long[] counts, double elapsedMilliseconds)
    {
        int index = Array.FindIndex(
            _latencyUpperBoundsMilliseconds,
            upperBound => elapsedMilliseconds <= upperBound);
        counts[index < 0 ? counts.Length - 1 : index]++;
    }

    private void Publish(GovernanceLoadDemonstrationSnapshot snapshot)
    {
        foreach (Channel<GovernanceLoadDemonstrationSnapshot> channel in _subscribers.Values)
        {
            _ = channel.Writer.TryWrite(snapshot);
        }
    }

    private void RemoveSubscription(Guid subscriptionId)
    {
        _ = _subscribers.TryRemove(subscriptionId, out _);
    }

    [LoggerMessage(
        EventId = 4601,
        Level = LogLevel.Error,
        Message = "Governance load demonstration {RunId} failed.")]
    private static partial void LogDemonstrationFailure(ILogger logger, string runId, Exception exception);

    private sealed class RunControl(string runId)
    {
        public string RunId { get; } = runId;

        public CancellationTokenSource Cancellation { get; } = new();

        public Task? Execution { get; set; }

        public int CancellationRequested;
    }
}
