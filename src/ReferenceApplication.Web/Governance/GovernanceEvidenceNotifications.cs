using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Identifies the persisted evidence represented by a live notification.
/// </summary>
public static class GovernanceEvidenceNotificationKinds
{
    /// <summary>
    /// A governance decision receipt persisted to the AsiBackbone audit ledger.
    /// </summary>
    public const string Decision = "decision";

    /// <summary>
    /// An NCAT mutation-audit batch committed with the application mutation.
    /// </summary>
    public const string MutationAudit = "mutation-audit";

    /// <summary>
    /// An AsiBackbone lifecycle event appended by audit-completion delivery.
    /// </summary>
    public const string LifecycleEvent = "lifecycle-event";
}

/// <summary>
/// Identifies newly persisted governance evidence without carrying its contents.
/// </summary>
/// <param name="CorrelationId">The request correlation identifier used to re-query the evidence API.</param>
/// <param name="Kind">The evidence kind.</param>
/// <param name="RecordId">The persisted record or batch identifier.</param>
public sealed record GovernanceEvidenceNotification(
    string CorrelationId,
    string Kind,
    string RecordId);

/// <summary>
/// Publishes and subscribes to best-effort, in-process governance evidence notifications.
/// </summary>
public interface IGovernanceEvidenceNotifier
{
    /// <summary>
    /// Publishes an identifier-only notification to current subscribers without waiting for them.
    /// </summary>
    /// <param name="notification">The persisted evidence notification.</param>
    void Publish(GovernanceEvidenceNotification notification);

    /// <summary>
    /// Creates a subscription for notifications published after this call returns.
    /// </summary>
    /// <returns>A live notification subscription.</returns>
    IGovernanceEvidenceSubscription Subscribe();
}

/// <summary>
/// Represents one live governance evidence notification subscription.
/// </summary>
public interface IGovernanceEvidenceSubscription : IAsyncDisposable
{
    /// <summary>
    /// Reads notifications until the caller cancels or disposes the subscription.
    /// </summary>
    /// <param name="cancellationToken">A token that ends the read.</param>
    /// <returns>The live notifications.</returns>
    IAsyncEnumerable<GovernanceEvidenceNotification> ReadAllAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Fans notifications out to bounded per-client channels. Slow clients may miss a hint and must re-query persisted
/// evidence; publishers are never delayed by a subscriber.
/// </summary>
internal sealed class InMemoryGovernanceEvidenceNotifier : IGovernanceEvidenceNotifier
{
    private const int _subscriberCapacity = 32;
    private readonly ConcurrentDictionary<Guid, Channel<GovernanceEvidenceNotification>> _subscribers = new();

    /// <inheritdoc />
    public void Publish(GovernanceEvidenceNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        foreach (Channel<GovernanceEvidenceNotification> channel in _subscribers.Values)
        {
            _ = channel.Writer.TryWrite(notification);
        }
    }

    /// <inheritdoc />
    public IGovernanceEvidenceSubscription Subscribe()
    {
        var channel = Channel.CreateBounded<GovernanceEvidenceNotification>(new BoundedChannelOptions(_subscriberCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        var subscriptionId = Guid.NewGuid();
        return _subscribers.TryAdd(subscriptionId, channel)
            ? new Subscription(_subscribers, subscriptionId, channel)
            : throw new InvalidOperationException("The governance evidence subscription could not be registered.");
    }

    private sealed class Subscription(
        ConcurrentDictionary<Guid, Channel<GovernanceEvidenceNotification>> subscriptions,
        Guid subscriptionId,
        Channel<GovernanceEvidenceNotification> channel) : IGovernanceEvidenceSubscription
    {
        private int _disposed;

        public IAsyncEnumerable<GovernanceEvidenceNotification> ReadAllAsync(
            CancellationToken cancellationToken = default)
        {
            return channel.Reader.ReadAllAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _ = subscriptions.TryRemove(subscriptionId, out _);
                channel.Writer.TryComplete();
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Isolates governance and outbox execution from notifier failures.
/// </summary>
public sealed partial class GovernanceEvidenceNotificationPublisher
{
    private readonly IGovernanceEvidenceNotifier _notifier;
    private readonly ILogger<GovernanceEvidenceNotificationPublisher> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GovernanceEvidenceNotificationPublisher" /> class.
    /// </summary>
    public GovernanceEvidenceNotificationPublisher(
        IGovernanceEvidenceNotifier notifier,
        ILogger<GovernanceEvidenceNotificationPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(logger);

        _notifier = notifier;
        _logger = logger;
    }

    /// <summary>
    /// Publishes a notification without allowing notifier failures to affect the completed persistence operation.
    /// </summary>
    /// <param name="notification">The identifier-only notification.</param>
    public void Publish(GovernanceEvidenceNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        try
        {
            _notifier.Publish(notification);
        }
        catch (Exception exception)
        {
            LogNotificationFailed(
                _logger,
                notification.Kind,
                notification.RecordId,
                exception.GetType().Name,
                exception);
        }
    }

    [LoggerMessage(
        EventId = 26004,
        Level = LogLevel.Warning,
        Message = "Governance evidence notification {EvidenceKind} for record {EvidenceRecordId} was not published ({ExceptionType}).")]
    private static partial void LogNotificationFailed(
        ILogger logger,
        string evidenceKind,
        string evidenceRecordId,
        string exceptionType,
        Exception exception);
}
