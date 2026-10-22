using ReferenceApplication.Web.Governance.FailureScenarios;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies that a failure-scenario run arms exactly one request: by its actor, before it expires, once, and that the
/// registry stays bounded.
/// </summary>
public sealed class FailureScenarioRegistryTests
{
    private const string _actor = "scenario-actor";
    private const string _correlationId = "scenario-correlation";

    private static readonly FailureScenarioRequest _request = new(
        "POST",
        FailureScenarioCatalog.RecordsPath,
        new Dictionary<string, string>(),
        new Dictionary<string, string>());

    /// <summary>
    /// Verifies that the armed request names its run in the run header, and that a run is consumed by one request only.
    /// </summary>
    [Fact]
    public void Run_IsConsumedByOneRequestOnly()
    {
        var registry = new FailureScenarioRegistry(new ManualTimeProvider());
        FailureScenarioRunState armed = registry.Arm(FailureScenarioCatalog.LedgerAppendFails, _actor, _request);

        Assert.Equal(armed.RunId, armed.Request!.Headers[FailureScenarioOptions.RunHeaderName]);
        Assert.Equal(FailureScenarioRunStatuses.Running, registry.TryBegin(armed.RunId, _actor, _correlationId)?.Status);
        Assert.Null(registry.TryBegin(armed.RunId, _actor, _correlationId));
        Assert.Null(registry.Find(armed.RunId)!.Request);
    }

    /// <summary>
    /// Verifies that a run cannot be consumed by another actor, and stays armed for its own.
    /// </summary>
    [Fact]
    public void Run_CannotBeConsumedByAnotherActor()
    {
        var registry = new FailureScenarioRegistry(new ManualTimeProvider());
        FailureScenarioRunState armed = registry.Arm(FailureScenarioCatalog.ConstraintThrows, _actor, _request);

        Assert.Null(registry.TryBegin(armed.RunId, "another-actor", _correlationId));
        Assert.NotNull(registry.TryBegin(armed.RunId, _actor, _correlationId));
    }

    /// <summary>
    /// Verifies that an unused run expires after the arm lifetime and can no longer be consumed.
    /// </summary>
    [Fact]
    public void Run_ExpiresUnused()
    {
        var clock = new ManualTimeProvider();
        var registry = new FailureScenarioRegistry(clock);
        FailureScenarioRunState armed = registry.Arm(FailureScenarioCatalog.ConstraintThrows, _actor, _request);

        clock.Advance(FailureScenarioOptions.ArmLifetime + TimeSpan.FromSeconds(1));

        Assert.Equal(FailureScenarioRunStatuses.Expired, registry.Find(armed.RunId)!.Status);
        Assert.Null(registry.TryBegin(armed.RunId, _actor, _correlationId));
    }

    /// <summary>
    /// Verifies that a delivery fault applies only to its request's correlation, until it is cleared.
    /// </summary>
    [Fact]
    public void DeliveryFault_AppliesToItsCorrelationUntilCleared()
    {
        var registry = new FailureScenarioRegistry(new ManualTimeProvider());
        FailureScenarioRunState armed = registry.Arm(FailureScenarioCatalog.OutboxDeliveryFails, _actor, _request);
        Assert.Null(registry.FindDeliveryFault(_correlationId));

        _ = registry.TryBegin(armed.RunId, _actor, _correlationId);

        Assert.Equal(armed.RunId, registry.FindDeliveryFault(_correlationId));
        Assert.Null(registry.FindDeliveryFault("another-correlation"));
        Assert.True(registry.ClearDeliveryFault(armed.RunId));
        Assert.Null(registry.FindDeliveryFault(_correlationId));
        Assert.False(registry.ClearDeliveryFault(armed.RunId));
    }

    /// <summary>
    /// Verifies that only the most recent runs are kept, so the oldest is forgotten when another is armed.
    /// </summary>
    [Fact]
    public void Registry_KeepsOnlyTheMostRecentRuns()
    {
        var registry = new FailureScenarioRegistry(new ManualTimeProvider());
        FailureScenarioRunState oldest = registry.Arm(FailureScenarioCatalog.ConstraintThrows, _actor, _request);

        for (int index = 0; index < FailureScenarioOptions.RetainedRunCount; index++)
        {
            _ = registry.Arm(FailureScenarioCatalog.ConstraintThrows, _actor, _request);
        }

        Assert.Null(registry.Find(oldest.RunId));
        Assert.Null(registry.TryBegin(oldest.RunId, _actor, _correlationId));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan duration)
        {
            _utcNow += duration;
        }
    }
}
