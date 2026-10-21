using AsiBackbone.EntityFrameworkCore.CapabilityGrants;
using Microsoft.Extensions.Options;

namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Purges consumed capability grants from AsiBackbone's EF Core use store once they are past their retention horizon.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EfCoreCapabilityGrantUseStore" /> keeps each use record until the grant expires plus its retention grace
/// period, so a replay is denied for as long as any validator could accept the grant. It never deletes records on its
/// own: scheduling <see cref="EfCoreCapabilityGrantUseStore.PurgeExpiredAsync" /> is the host's job. This service runs
/// it every <see cref="CapabilityTokenOptions.UseStorePurgeIntervalSeconds" />.
/// </para>
/// <para>
/// The first purge runs one interval after startup, so starting the application never touches a database that has not
/// been migrated yet. Purging only removes records the store would no longer consult, so several replicas may run it
/// concurrently, and a failed cycle is logged and retried at the next interval.
/// </para>
/// </remarks>
public sealed partial class CapabilityGrantUsePurgeService(
    IServiceScopeFactory scopeFactory,
    IOptions<CapabilityTokenOptions> options,
    TimeProvider timeProvider,
    ILogger<CapabilityGrantUsePurgeService> logger)
    : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly IOptions<CapabilityTokenOptions> _options =
        options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<CapabilityGrantUsePurgeService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    [LoggerMessage(
        EventId = 19120,
        Level = LogLevel.Information,
        Message = "Purged {PurgedCount} capability grant use records past their retention horizon.")]
    private static partial void LogPurged(ILogger logger, int purgedCount);

    [LoggerMessage(
        EventId = 19121,
        Level = LogLevel.Warning,
        Message = "The capability grant use purge failed and will run again in {IntervalSeconds} seconds.")]
    private static partial void LogPurgeFailure(ILogger logger, Exception exception, double intervalSeconds);

    /// <summary>
    /// Purges use records whose retention horizon has passed.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the purge.</param>
    /// <returns>The number of records removed.</returns>
    public async Task<int> PurgeOnceAsync(CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        EfCoreCapabilityGrantUseStore store = scope.ServiceProvider.GetRequiredService<EfCoreCapabilityGrantUseStore>();

        return await store
            .PurgeExpiredAsync(_timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.Value.UseStorePurgeIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, _timeProvider, stoppingToken).ConfigureAwait(false);

                int purgedCount = await PurgeOnceAsync(stoppingToken).ConfigureAwait(false);
                if (purgedCount > 0)
                {
                    LogPurged(_logger, purgedCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogPurgeFailure(_logger, exception, interval.TotalSeconds);
            }
        }
    }
}
