using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace ReferenceApplication.Web.Tests.Infrastructure;

/// <summary>
/// Captures what the host's OpenTelemetry pipeline exports, through an in-memory span exporter and an in-memory
/// metric exporter added to the host's own tracer and meter providers.
/// </summary>
/// <remarks>
/// The exporters see only the activity sources and meters the host's configuration adds, so a test that finds a signal
/// here proves the host exports it, not merely that something created it. Exported items are copied, because the SDK
/// reuses <see cref="Metric" /> instances between collections.
/// </remarks>
internal sealed class InMemoryTelemetryExporters : IDisposable
{
    private readonly SpanExporter _spans = new();
    private readonly MetricExporter _metrics = new();

    /// <summary>
    /// Gets the exported spans, oldest first.
    /// </summary>
    public IReadOnlyList<ExportedSpan> Spans => [.. _spans.Items];

    /// <summary>
    /// Gets the exported metric points from every collection, oldest first.
    /// </summary>
    public IReadOnlyList<ExportedMetricPoint> MetricPoints => [.. _metrics.Items];

    /// <summary>
    /// Adds the exporters to the host's tracer and meter providers.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    public void AddTo(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _ = services.ConfigureOpenTelemetryTracerProvider(tracing =>
            tracing.AddProcessor(new SimpleActivityExportProcessor(_spans)));
        _ = services.ConfigureOpenTelemetryMeterProvider(metrics =>
            metrics.AddReader(new BaseExportingMetricReader(_metrics)));
    }

    /// <summary>
    /// Collects and exports the host's current metrics, including observable gauges.
    /// </summary>
    /// <param name="services">The host's service provider.</param>
    public static void CollectMetrics(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        Assert.True(
            services.GetRequiredService<MeterProvider>().ForceFlush(),
            "The host's meter provider did not flush.");
    }

    /// <summary>
    /// An exported span.
    /// </summary>
    /// <param name="Source">The activity source name.</param>
    /// <param name="Name">The span's display name.</param>
    /// <param name="Kind">The span kind.</param>
    /// <param name="TraceId">The W3C trace identifier.</param>
    /// <param name="SpanId">The span identifier.</param>
    /// <param name="ParentSpanId">The parent span identifier, when the span has one.</param>
    /// <param name="Tags">The span's tags, as strings.</param>
    /// <param name="EventNames">The names of the span's events.</param>
    internal sealed record ExportedSpan(
        string Source,
        string Name,
        ActivityKind Kind,
        string TraceId,
        string SpanId,
        string? ParentSpanId,
        IReadOnlyDictionary<string, string> Tags,
        IReadOnlyList<string> EventNames);

    /// <summary>
    /// An exported metric point.
    /// </summary>
    /// <param name="Meter">The meter name.</param>
    /// <param name="Instrument">The instrument name.</param>
    /// <param name="Type">The metric type.</param>
    /// <param name="Value">The sum or last value; for a histogram, its sum.</param>
    /// <param name="Count">For a histogram, its count; otherwise 1.</param>
    /// <param name="Tags">The point's tags, as strings.</param>
    internal sealed record ExportedMetricPoint(
        string Meter,
        string Instrument,
        MetricType Type,
        double Value,
        long Count,
        IReadOnlyDictionary<string, string> Tags);

    private sealed class SpanExporter : BaseExporter<Activity>
    {
        public ConcurrentQueue<ExportedSpan> Items { get; } = new();

        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (Activity activity in batch)
            {
                Dictionary<string, string> tags = new(StringComparer.Ordinal);
                foreach (KeyValuePair<string, object?> tag in activity.TagObjects)
                {
                    tags[tag.Key] = Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                }

                Items.Enqueue(new ExportedSpan(
                    activity.Source.Name,
                    activity.DisplayName,
                    activity.Kind,
                    activity.TraceId.ToHexString(),
                    activity.SpanId.ToHexString(),
                    activity.ParentSpanId == default ? null : activity.ParentSpanId.ToHexString(),
                    tags,
                    [.. activity.Events.Select(static activityEvent => activityEvent.Name)]));
            }

            return ExportResult.Success;
        }
    }

    private sealed class MetricExporter : BaseExporter<Metric>
    {
        public ConcurrentQueue<ExportedMetricPoint> Items { get; } = new();

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (Metric metric in batch)
            {
                foreach (ref readonly MetricPoint point in metric.GetMetricPoints())
                {
                    Dictionary<string, string> tags = new(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, object?> tag in point.Tags)
                    {
                        tags[tag.Key] = Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                    }

                    (double value, long count) = metric.MetricType switch
                    {
                        MetricType.LongSum or MetricType.LongSumNonMonotonic => (point.GetSumLong(), 1L),
                        MetricType.DoubleSum or MetricType.DoubleSumNonMonotonic => (point.GetSumDouble(), 1L),
                        MetricType.LongGauge => (point.GetGaugeLastValueLong(), 1L),
                        MetricType.DoubleGauge => (point.GetGaugeLastValueDouble(), 1L),
                        MetricType.Histogram => (point.GetHistogramSum(), point.GetHistogramCount()),
                        MetricType.Histogram or MetricType.ExponentialHistogram =>
                            (point.GetHistogramSum(), point.GetHistogramCount()),
                        _ => (double.NaN, 0L)
                    };

                    Items.Enqueue(new ExportedMetricPoint(
                        metric.MeterName,
                        metric.Name,
                        metric.MetricType,
                        value,
                        count,
                        tags));
                }
            }

            return ExportResult.Success;
        }
    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }
}
