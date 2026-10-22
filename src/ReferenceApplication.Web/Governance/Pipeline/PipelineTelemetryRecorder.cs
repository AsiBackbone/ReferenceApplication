using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.Options;
using ReferenceApplication.Infrastructure.Data.Auditing;
using ReferenceApplication.Web.Options;

namespace ReferenceApplication.Web.Governance.Pipeline;

/// <summary>
/// Observes the process's spans and metric instruments for the Development pipeline inspector, without creating
/// telemetry of its own.
/// </summary>
/// <remarks>
/// <para>
/// The activity listener never samples, so it causes no span to be created; it records only spans that another
/// listener, normally the host's OpenTelemetry tracing, has already created. The meter listener enables measurements
/// only for the host's own observable reconciliation gauges, which it reads on demand off the request path, and for
/// the ASP.NET Core request-duration histogram when the host's OpenTelemetry metrics already collect it.
/// </para>
/// <para>
/// Spans and measurements are kept in bounded in-memory buffers of
/// <see cref="PipelineInspectorOptions.TelemetryCapacity" /> entries each. Nothing is read back from an exporter.
/// </para>
/// </remarks>
internal sealed class PipelineTelemetryRecorder(
    IOptions<ApplicationOpenTelemetryOptions> telemetryOptions,
    TimeProvider timeProvider) : IHostedService, IDisposable
{
    /// <summary>
    /// Gets the name of the meter the host creates for NCAT audit reconciliation.
    /// </summary>
    public const string ReconciliationMeterName = ApplicationAuditReconciliationMetrics.MeterName;

    /// <summary>
    /// Gets the name of the ASP.NET Core hosting meter.
    /// </summary>
    public const string AspNetCoreHostingMeterName = "Microsoft.AspNetCore.Hosting";

    /// <summary>
    /// Gets the name of the ASP.NET Core request-duration histogram.
    /// </summary>
    public const string RequestDurationInstrumentName = "http.server.request.duration";

    private static readonly string[] _spanTagKeys =
        ["http.request.method", "http.route", "http.response.status_code", "url.path", "error.type"];

    private static readonly string[] _measurementTagKeys =
        ["http.request.method", "http.route", "http.response.status_code"];

    private readonly ApplicationOpenTelemetryOptions _telemetryOptions =
        telemetryOptions?.Value ?? throw new ArgumentNullException(nameof(telemetryOptions));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly Lock _gate = new();
    private readonly Lock _gaugeGate = new();
    private readonly Queue<PipelineSpan> _spans = new();
    private readonly Queue<PipelineMeasurement> _measurements = new();
    private readonly ConcurrentDictionary<string, SourceEntry> _sources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MeterEntry> _meters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Meter, string Instrument), double> _gauges = new();
    private ActivityListener? _activityListener;
    private MeterListener? _meterListener;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source =>
            {
                _ = _sources.GetOrAdd(source.Name, static (_, version) => new SourceEntry(version), source.Version);
                return true;
            },
            Sample = static (ref options) => ActivitySamplingResult.None,
            SampleUsingParentId = static (ref options) => ActivitySamplingResult.None,
            ActivityStopped = RecordSpan
        };
        ActivitySource.AddActivityListener(_activityListener);

        _meterListener = new MeterListener { InstrumentPublished = OnInstrumentPublished };
        _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
            RecordMeasurement(instrument, value, tags));
        _meterListener.SetMeasurementEventCallback<float>((instrument, value, tags, state) =>
            RecordMeasurement(instrument, value, tags));
        _meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
            RecordMeasurement(instrument, value, tags));
        _meterListener.SetMeasurementEventCallback<int>((instrument, value, tags, state) =>
            RecordMeasurement(instrument, value, tags));
        _meterListener.Start();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _activityListener?.Dispose();
        _activityListener = null;
        _meterListener?.Dispose();
        _meterListener = null;
    }

    /// <summary>
    /// Gets the observed spans of one trace, oldest first.
    /// </summary>
    /// <param name="traceId">The W3C trace identifier, as 32 hexadecimal characters.</param>
    /// <returns>The spans.</returns>
    public IReadOnlyList<PipelineSpan> GetSpans(string traceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);

        lock (_gate)
        {
            return [.. _spans.Where(span => string.Equals(span.TraceId, traceId, StringComparison.OrdinalIgnoreCase))];
        }
    }

    /// <summary>
    /// Gets the observed measurements of one instrument, oldest first.
    /// </summary>
    /// <param name="meterName">The meter name.</param>
    /// <param name="instrumentName">The instrument name.</param>
    /// <returns>The measurements.</returns>
    public IReadOnlyList<PipelineMeasurement> GetMeasurements(string meterName, string instrumentName)
    {
        lock (_gate)
        {
            return [.. _measurements.Where(measurement =>
                string.Equals(measurement.Meter, meterName, StringComparison.Ordinal) &&
                string.Equals(measurement.Instrument, instrumentName, StringComparison.Ordinal))];
        }
    }

    /// <summary>
    /// Gets the activity sources that exist in the process.
    /// </summary>
    /// <param name="addedToOpenTelemetry">Whether the host's OpenTelemetry configuration collects a source.</param>
    /// <returns>The sources, by name.</returns>
    public IReadOnlyList<PipelineActivitySourceInfo> GetActivitySources(Func<string, bool> addedToOpenTelemetry)
    {
        ArgumentNullException.ThrowIfNull(addedToOpenTelemetry);

        return [.. _sources
            .OrderBy(static source => source.Key, StringComparer.Ordinal)
            .Select(source => new PipelineActivitySourceInfo(
                source.Key,
                source.Value.Version,
                addedToOpenTelemetry(source.Key),
                source.Value.SpansObserved))];
    }

    /// <summary>
    /// Gets the meters that exist in the process.
    /// </summary>
    /// <param name="addedToOpenTelemetry">Whether the host's OpenTelemetry configuration collects a meter.</param>
    /// <returns>The meters, by name.</returns>
    public IReadOnlyList<PipelineMeterInfo> GetMeters(Func<string, bool> addedToOpenTelemetry)
    {
        ArgumentNullException.ThrowIfNull(addedToOpenTelemetry);

        return [.. _meters
            .OrderBy(static meter => meter.Key, StringComparer.Ordinal)
            .Select(meter => new PipelineMeterInfo(
                meter.Key,
                meter.Value.Version,
                addedToOpenTelemetry(meter.Key),
                meter.Value.GetInstruments()))];
    }

    /// <summary>
    /// Reads the current values of the host's own observable gauges.
    /// </summary>
    /// <returns>The readings, by meter and instrument.</returns>
    /// <remarks>Reading runs the gauges' callbacks on the calling thread, which is never a governed request.</remarks>
    public IReadOnlyList<PipelineGaugeReading> ReadHostGauges()
    {
        lock (_gaugeGate)
        {
            _meterListener?.RecordObservableInstruments();
        }

        return [.. _gauges
            .OrderBy(static gauge => gauge.Key.Meter, StringComparer.Ordinal)
            .ThenBy(static gauge => gauge.Key.Instrument, StringComparer.Ordinal)
            .Select(static gauge => new PipelineGaugeReading(gauge.Key.Meter, gauge.Key.Instrument, gauge.Value))];
    }

    private void OnInstrumentPublished(Instrument instrument, MeterListener listener)
    {
        MeterEntry meter = _meters.GetOrAdd(
            instrument.Meter.Name,
            static (_, version) => new MeterEntry(version),
            instrument.Meter.Version);
        meter.Add(new PipelineInstrumentInfo(instrument.Name, KindOf(instrument), instrument.Unit));

        if (ShouldObserve(instrument))
        {
            listener.EnableMeasurementEvents(instrument);
        }
    }

    private bool ShouldObserve(Instrument instrument)
    {
        // Enabling measurements on an instrument nothing else collects would make it start recording, so only the
        // host's gauges, read on demand, and a histogram OpenTelemetry already collects are observed.
        bool hostGauge = instrument.IsObservable &&
            string.Equals(instrument.Meter.Name, ReconciliationMeterName, StringComparison.Ordinal);
        bool collectedRequestDuration = _telemetryOptions.Enabled &&
            _telemetryOptions.EnableMetrics &&
            _telemetryOptions.EnableAspNetCoreInstrumentation &&
            string.Equals(instrument.Meter.Name, AspNetCoreHostingMeterName, StringComparison.Ordinal) &&
            string.Equals(instrument.Name, RequestDurationInstrumentName, StringComparison.Ordinal);

        return hostGauge || collectedRequestDuration;
    }

    private void RecordSpan(Activity activity)
    {
        if (_sources.TryGetValue(activity.Source.Name, out SourceEntry? source))
        {
            source.Increment();
        }

        Dictionary<string, string> tags = new(StringComparer.Ordinal);
        foreach (string key in _spanTagKeys)
        {
            if (activity.GetTagItem(key) is { } value)
            {
                tags[key] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }

        Enqueue(_spans, new PipelineSpan(
            Source: activity.Source.Name,
            Name: activity.DisplayName,
            TraceId: activity.TraceId.ToHexString(),
            SpanId: activity.SpanId.ToHexString(),
            ParentSpanId: activity.ParentSpanId == default ? null : activity.ParentSpanId.ToHexString(),
            Kind: activity.Kind.ToString(),
            StartUtc: new DateTimeOffset(DateTime.SpecifyKind(activity.StartTimeUtc, DateTimeKind.Utc)),
            DurationMilliseconds: activity.Duration.TotalMilliseconds,
            Status: activity.Status.ToString(),
            Tags: tags));
    }

    private void RecordMeasurement(
        Instrument instrument,
        double value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (instrument.IsObservable)
        {
            _gauges[(instrument.Meter.Name, instrument.Name)] = value;
            return;
        }

        Dictionary<string, string> copied = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Value is not null && Array.IndexOf(_measurementTagKeys, tag.Key) >= 0)
            {
                copied[tag.Key] = Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }

        Enqueue(_measurements, new PipelineMeasurement(
            instrument.Meter.Name,
            instrument.Name,
            value,
            instrument.Unit,
            _timeProvider.GetUtcNow(),
            copied));
    }

    private void Enqueue<T>(Queue<T> queue, T item)
    {
        lock (_gate)
        {
            queue.Enqueue(item);
            while (queue.Count > PipelineInspectorOptions.TelemetryCapacity)
            {
                _ = queue.Dequeue();
            }
        }
    }

    private static string KindOf(Instrument instrument)
    {
        string name = instrument.GetType().Name;
        int genericMarker = name.IndexOf('`', StringComparison.Ordinal);
        return genericMarker < 0 ? name : name[..genericMarker];
    }

    private sealed class SourceEntry(string? version)
    {
        private long _spansObserved;

        public string? Version { get; } = version;

        public long SpansObserved => Interlocked.Read(ref _spansObserved);

        public void Increment()
        {
            _ = Interlocked.Increment(ref _spansObserved);
        }
    }

    private sealed class MeterEntry(string? version)
    {
        private readonly ConcurrentDictionary<string, PipelineInstrumentInfo> _instruments = new(StringComparer.Ordinal);

        public string? Version { get; } = version;

        public void Add(PipelineInstrumentInfo instrument)
        {
            _instruments[instrument.Name] = instrument;
        }

        public IReadOnlyList<PipelineInstrumentInfo> GetInstruments()
        {
            return [.. _instruments.Values.OrderBy(static instrument => instrument.Name, StringComparer.Ordinal)];
        }
    }
}
