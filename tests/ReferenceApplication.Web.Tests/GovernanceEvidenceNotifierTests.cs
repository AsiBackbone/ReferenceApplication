using Microsoft.Extensions.Logging;
using ReferenceApplication.Web.Governance;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies the in-memory fan-out that backs the Step 7 evidence notification stream, and the publisher that isolates
/// governance and outbox execution from notifier failures.
/// </summary>
public sealed class GovernanceEvidenceNotifierTests
{
    private const int _subscriberCapacity = 32;

    /// <summary>
    /// Verifies that every active subscriber receives each notification in publication order.
    /// </summary>
    [Fact]
    public async Task Publish_MultipleSubscribers_DeliversToEachInOrder()
    {
        var notifier = new InMemoryGovernanceEvidenceNotifier();
        IGovernanceEvidenceSubscription first = notifier.Subscribe();
        IGovernanceEvidenceSubscription second = notifier.Subscribe();

        notifier.Publish(CreateNotification(1));
        notifier.Publish(CreateNotification(2));
        await first.DisposeAsync();
        await second.DisposeAsync();

        Assert.Equal(["record-1", "record-2"], await ReadRecordIdsAsync(first));
        Assert.Equal(["record-1", "record-2"], await ReadRecordIdsAsync(second));
    }

    /// <summary>
    /// Verifies that a subscriber that stops reading keeps only the newest notifications, so a slow client can never
    /// delay a publisher or grow memory without bound.
    /// </summary>
    [Fact]
    public async Task Publish_SubscriberAtCapacity_DropsOldestWithoutBlocking()
    {
        var notifier = new InMemoryGovernanceEvidenceNotifier();
        IGovernanceEvidenceSubscription subscription = notifier.Subscribe();

        for (int index = 0; index < _subscriberCapacity + 8; index++)
        {
            notifier.Publish(CreateNotification(index));
        }

        await subscription.DisposeAsync();
        IReadOnlyList<string> received = await ReadRecordIdsAsync(subscription);

        Assert.Equal(_subscriberCapacity, received.Count);
        Assert.Equal("record-8", received[0]);
        Assert.Equal($"record-{_subscriberCapacity + 7}", received[^1]);
    }

    /// <summary>
    /// Verifies that disposing a subscription removes it from the fan-out without affecting other subscribers, and
    /// that repeated disposal is harmless.
    /// </summary>
    [Fact]
    public async Task Dispose_Subscription_StopsDeliveryAndLeavesOthersSubscribed()
    {
        var notifier = new InMemoryGovernanceEvidenceNotifier();
        IGovernanceEvidenceSubscription departed = notifier.Subscribe();
        IGovernanceEvidenceSubscription remaining = notifier.Subscribe();

        notifier.Publish(CreateNotification(1));
        await departed.DisposeAsync();
        await departed.DisposeAsync();
        notifier.Publish(CreateNotification(2));
        await remaining.DisposeAsync();

        Assert.Equal(["record-1"], await ReadRecordIdsAsync(departed));
        Assert.Equal(["record-1", "record-2"], await ReadRecordIdsAsync(remaining));
    }

    /// <summary>
    /// Verifies that publishing with no subscribers is a no-op and that a missing notification is rejected.
    /// </summary>
    [Fact]
    public void Publish_NoSubscribers_IsNoOpAndRejectsNull()
    {
        var notifier = new InMemoryGovernanceEvidenceNotifier();

        notifier.Publish(CreateNotification(1));

        _ = Assert.Throws<ArgumentNullException>(() => notifier.Publish(null!));
    }

    /// <summary>
    /// Verifies that the publisher contains a failing notifier and records only identifiers in its warning.
    /// </summary>
    [Fact]
    public void Publisher_ThrowingNotifier_LogsWarningWithoutThrowing()
    {
        var logger = new RecordingLogger<GovernanceEvidenceNotificationPublisher>();
        var publisher = new GovernanceEvidenceNotificationPublisher(new ThrowingNotifier(), logger);

        publisher.Publish(CreateNotification(7));

        (LogLevel level, EventId eventId, string message, Exception? exception) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Equal(26004, eventId.Id);
        Assert.Contains(GovernanceEvidenceNotificationKinds.Decision, message, StringComparison.Ordinal);
        Assert.Contains("record-7", message, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), message, StringComparison.Ordinal);
        Assert.DoesNotContain("correlation-7", message, StringComparison.Ordinal);
        _ = Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>
    /// Verifies that the publisher forwards notifications unchanged and still rejects a missing notification.
    /// </summary>
    [Fact]
    public async Task Publisher_WorkingNotifier_ForwardsNotification()
    {
        var notifier = new InMemoryGovernanceEvidenceNotifier();
        var logger = new RecordingLogger<GovernanceEvidenceNotificationPublisher>();
        var publisher = new GovernanceEvidenceNotificationPublisher(notifier, logger);
        IGovernanceEvidenceSubscription subscription = notifier.Subscribe();

        publisher.Publish(CreateNotification(3));
        await subscription.DisposeAsync();

        Assert.Equal(["record-3"], await ReadRecordIdsAsync(subscription));
        Assert.Empty(logger.Entries);
        _ = Assert.Throws<ArgumentNullException>(() => publisher.Publish(null!));
    }

    private static GovernanceEvidenceNotification CreateNotification(int index)
    {
        return new GovernanceEvidenceNotification(
            $"correlation-{index}",
            GovernanceEvidenceNotificationKinds.Decision,
            $"record-{index}");
    }

    private static async Task<IReadOnlyList<string>> ReadRecordIdsAsync(IGovernanceEvidenceSubscription subscription)
    {
        // Disposal completes the subscription's channel, so reading drains what was buffered and then ends.
        List<string> recordIds = [];

        await foreach (GovernanceEvidenceNotification notification in subscription
            .ReadAllAsync(TestContext.Current.CancellationToken))
        {
            recordIds.Add(notification.RecordId);
        }

        return recordIds;
    }

    private sealed class ThrowingNotifier : IGovernanceEvidenceNotifier
    {
        public void Publish(GovernanceEvidenceNotification notification)
        {
            throw new InvalidOperationException("Notifier unavailable.");
        }

        public IGovernanceEvidenceSubscription Subscribe()
        {
            throw new NotSupportedException();
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, EventId EventId, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, eventId, formatter(state, exception), exception));
        }
    }
}
