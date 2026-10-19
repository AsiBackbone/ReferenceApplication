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
        var controller = new GovernanceEvidenceController(
            new StubEvidenceQuery(),
            new StubEvidenceVerifier(),
            new SingleNotificationNotifier(notification));
        var responseBody = new MemoryStream();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                Response = { Body = responseBody }
            }
        };
        using var disconnected = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await controller.StreamNotifications(" correlation-24 ", disconnected.Token);

        Assert.Equal("text/event-stream", controller.Response.ContentType);
        Assert.Equal("no-cache, no-store", controller.Response.Headers.CacheControl);
        string payload = Encoding.UTF8.GetString(responseBody.ToArray());
        Assert.StartsWith("event: evidence\n", payload, StringComparison.Ordinal);
        Assert.Contains(
            "data: {\"correlationId\":\"correlation-24\",\"kind\":\"decision\",\"recordId\":\"record-24\"}\n\n",
            payload,
            StringComparison.Ordinal);
        Assert.DoesNotContain("actor", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outcome", payload, StringComparison.OrdinalIgnoreCase);
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

    private sealed class SingleNotificationNotifier(GovernanceEvidenceNotification notification)
        : IGovernanceEvidenceNotifier
    {
        public void Publish(GovernanceEvidenceNotification value)
        {
        }

        public IGovernanceEvidenceSubscription Subscribe()
        {
            return new SingleNotificationSubscription(notification);
        }
    }

    private sealed class SingleNotificationSubscription(GovernanceEvidenceNotification notification)
        : IGovernanceEvidenceSubscription
    {
        public async IAsyncEnumerable<GovernanceEvidenceNotification> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return notification;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
