using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ReferenceApplication.Web.Controllers.Api;
using ReferenceApplication.Web.Governance;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies the Server-Sent Events representation and disconnect behavior independently of TestServer, which buffers
/// open streaming responses until they end.
/// </summary>
public sealed class GovernanceEvidenceControllerTests
{
    /// <summary>
    /// Verifies that the stream emits only the identifier notification as an SSE event and treats cancellation as a
    /// normal client disconnect.
    /// </summary>
    [Fact]
    public async Task StreamNotifications_MatchingNotification_WritesSseAndHandlesDisconnect()
    {
        var notification = new GovernanceEvidenceNotification(
            "correlation-24",
            GovernanceEvidenceNotificationKinds.Decision,
            "record-24");
        (GovernanceEvidenceController controller, MemoryStream responseBody) = CreateController(
            new SequenceNotifier(notification),
            TimeProvider.System);
        using var disconnected = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await controller.StreamNotifications(" correlation-24 ", disconnected.Token);

        Assert.Equal("text/event-stream", controller.Response.ContentType);
        Assert.Equal("no-cache, no-store", controller.Response.Headers.CacheControl);
        Assert.Equal("no", controller.Response.Headers["X-Accel-Buffering"]);
        string payload = Encoding.UTF8.GetString(responseBody.ToArray());
        Assert.StartsWith(": connected\n\nevent: evidence\n", payload, StringComparison.Ordinal);
        Assert.Contains(
            "data: {\"correlationId\":\"correlation-24\",\"kind\":\"decision\",\"recordId\":\"record-24\"}\n\n",
            payload,
            StringComparison.Ordinal);
        Assert.DoesNotContain("actor", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outcome", payload, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that a correlation filter suppresses other requests' notifications, including ones that differ only by
    /// case, while an unfiltered stream relays every notification in order.
    /// </summary>
    [Fact]
    public async Task StreamNotifications_CorrelationFilter_RelaysOnlyExactMatches()
    {
        GovernanceEvidenceNotification[] notifications =
        [
            new("correlation-a", GovernanceEvidenceNotificationKinds.Decision, "record-1"),
            new("correlation-b", GovernanceEvidenceNotificationKinds.MutationAudit, "record-2"),
            new("CORRELATION-A", GovernanceEvidenceNotificationKinds.MutationAudit, "record-3"),
            new("correlation-a", GovernanceEvidenceNotificationKinds.LifecycleEvent, "record-4")
        ];

        string filtered = await StreamAsync("correlation-a", notifications);
        string unfiltered = await StreamAsync("   ", notifications);

        Assert.Equal(["record-1", "record-4"], ReadEventRecordIds(filtered));
        Assert.Equal(["record-1", "record-2", "record-3", "record-4"], ReadEventRecordIds(unfiltered));
    }

    /// <summary>
    /// Verifies that an idle stream emits comment heartbeats at the documented interval and that heartbeats and events
    /// written concurrently never interleave within an SSE frame.
    /// </summary>
    [Fact]
    public async Task StreamNotifications_IdleAndActive_WritesWholeHeartbeatAndEventFrames()
    {
        var timeProvider = new AcceleratedTimeProvider();
        GovernanceEvidenceNotification[] notifications = [.. Enumerable
            .Range(0, 20)
            .Select(index => new GovernanceEvidenceNotification(
                "correlation-heartbeat",
                GovernanceEvidenceNotificationKinds.Decision,
                $"record-{index}"))];
        using var disconnected = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        (GovernanceEvidenceController controller, MemoryStream responseBody) = CreateController(
            new SequenceNotifier(
                TimeSpan.FromMilliseconds(5),
                // Stay connected long enough after the last event for further heartbeats to be written.
                () => disconnected.CancelAfter(TimeSpan.FromMilliseconds(100)),
                notifications),
            timeProvider);

        await controller.StreamNotifications(null, disconnected.Token);

        Assert.Equal(GovernanceEvidenceController.HeartbeatInterval, timeProvider.RequestedPeriod);
        string payload = Encoding.UTF8.GetString(responseBody.ToArray());
        Assert.EndsWith("\n\n", payload, StringComparison.Ordinal);
        string[] frames = payload[..^2].Split("\n\n");
        Assert.Equal(": connected", frames[0]);
        Assert.Contains(": keep-alive", frames);
        Assert.All(frames.Skip(1), frame => Assert.True(
            frame == ": keep-alive" ||
            (frame.StartsWith("event: evidence\ndata: {", StringComparison.Ordinal) &&
             frame.EndsWith('}') &&
             frame.Count(character => character == '\n') == 1),
            $"Unexpected SSE frame: {frame}"));
        Assert.Equal(
            notifications.Select(notification => notification.RecordId),
            ReadEventRecordIds(payload));
    }

    /// <summary>
    /// Verifies that the controller requires every collaborator, including the clock that drives heartbeats.
    /// </summary>
    [Fact]
    public void Constructor_MissingTimeProvider_Throws()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => new GovernanceEvidenceController(
            new StubEvidenceQuery(),
            new StubEvidenceVerifier(),
            new SequenceNotifier(),
            null!));

        Assert.Equal("timeProvider", exception.ParamName);
    }

    private static async Task<string> StreamAsync(
        string? correlationId,
        IReadOnlyList<GovernanceEvidenceNotification> notifications)
    {
        (GovernanceEvidenceController controller, MemoryStream responseBody) = CreateController(
            new SequenceNotifier([.. notifications]),
            TimeProvider.System);
        using var disconnected = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await controller.StreamNotifications(correlationId, disconnected.Token);

        return Encoding.UTF8.GetString(responseBody.ToArray());
    }

    private static (GovernanceEvidenceController Controller, MemoryStream ResponseBody) CreateController(
        IGovernanceEvidenceNotifier notifier,
        TimeProvider timeProvider)
    {
        var controller = new GovernanceEvidenceController(
            new StubEvidenceQuery(),
            new StubEvidenceVerifier(),
            notifier,
            timeProvider);
        var responseBody = new MemoryStream();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                Response = { Body = responseBody }
            }
        };

        return (controller, responseBody);
    }

    private static string[] ReadEventRecordIds(string payload)
    {
        return [.. payload
            .Split('\n')
            .Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
            .Select(line => System.Text.Json.JsonDocument.Parse(line["data: ".Length..])
                .RootElement
                .GetProperty("recordId")
                .GetString()!)];
    }

    private sealed class StubEvidenceQuery : IGovernanceEvidenceQuery
    {
        public ValueTask<GovernanceEvidenceTimelineResponse?> FindAsync(
            string correlationId,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<GovernanceEvidenceTimelineResponse?>(null);
        }
    }

    private sealed class StubEvidenceVerifier : IGovernanceEvidenceVerifier
    {
        public ValueTask<GovernanceEvidenceVerificationResponse?> VerifyAsync(
            string correlationId,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<GovernanceEvidenceVerificationResponse?>(null);
        }
    }

    private sealed class SequenceNotifier(
        TimeSpan delay,
        Action? onDrained,
        params GovernanceEvidenceNotification[] notifications) : IGovernanceEvidenceNotifier
    {
        public SequenceNotifier(params GovernanceEvidenceNotification[] notifications)
            : this(TimeSpan.Zero, null, notifications)
        {
        }

        public void Publish(GovernanceEvidenceNotification value)
        {
        }

        public IGovernanceEvidenceSubscription Subscribe()
        {
            return new SequenceSubscription(delay, onDrained, notifications);
        }
    }

    private sealed class SequenceSubscription(
        TimeSpan delay,
        Action? onDrained,
        GovernanceEvidenceNotification[] notifications) : IGovernanceEvidenceSubscription
    {
        public async IAsyncEnumerable<GovernanceEvidenceNotification> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (GovernanceEvidenceNotification notification in notifications)
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                yield return notification;
            }

            onDrained?.Invoke();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Records the requested timer period and fires it every few milliseconds, so heartbeat behavior is observable
    /// without waiting for the production interval.
    /// </summary>
    private sealed class AcceleratedTimeProvider : TimeProvider
    {
        private static readonly TimeSpan _acceleratedPeriod = TimeSpan.FromMilliseconds(3);

        public TimeSpan? RequestedPeriod { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            RequestedPeriod = period;
            return System.CreateTimer(callback, state, Accelerate(dueTime), Accelerate(period));
        }

        private static TimeSpan Accelerate(TimeSpan value)
        {
            return value == Timeout.InfiniteTimeSpan || value == TimeSpan.Zero ? value : _acceleratedPeriod;
        }
    }
}
