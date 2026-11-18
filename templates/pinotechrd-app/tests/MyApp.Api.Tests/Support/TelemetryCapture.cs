using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace MyApp.Api.Tests.Support;

/// <summary>A span as the host's configured tracer provider recorded it.</summary>
internal sealed record CapturedSpan(
    string Source,
    string Name,
    string TraceId,
    ActivityKind Kind,
    IReadOnlyDictionary<string, string> Tags,
    IReadOnlyList<string> Texts,
    ActivityStatusCode Status,
    string? StatusDescription,
    IReadOnlyList<CapturedEvent> Events);

/// <summary>An event recorded on a span (the <c>exception</c> event, for one) with its attributes as text.</summary>
internal sealed record CapturedEvent(string Name, IReadOnlyDictionary<string, string> Attributes);

/// <summary>One metric point: its tags and, for a histogram, how many measurements it holds.</summary>
internal sealed record CapturedPoint(IReadOnlyDictionary<string, string> Tags, long Count);

/// <summary>A metric as the host's configured meter provider collected it.</summary>
internal sealed record CapturedMetric(string Meter, string Name, IReadOnlyList<CapturedPoint> Points);

/// <summary>
/// An in-memory exporter in the host's own OpenTelemetry providers, written with the SDK's base classes so the tests
/// need no exporter package. It sees exactly what the application's configuration lets through. Activity sources and
/// meters are process-wide, so a provider also sees what other hosts of the test process do: tests pick their data
/// by a trace id they chose or by a source or meter name that is theirs alone.
/// </summary>
internal sealed class TelemetryCapture : IDisposable
{
    private const string ServerSourceName = "Microsoft.AspNetCore";

    private readonly SpanProcessor _spans = new();
    private readonly MetricExporter _exporter = new();
    private readonly ConcurrentQueue<string> _stopped = new();
    private readonly ActivityListener _listener;

    public TelemetryCapture()
    {
        Reader = new BaseExportingMetricReader(_exporter);
        // Sees every server activity when it stops, recorded or not, so a test can tell that a request is over before
        // it asserts that nothing was recorded for it.
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ServerSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => _stopped.Enqueue(activity.TraceId.ToHexString()),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>The trace ids of the server activities that have stopped, whether or not they were recorded.</summary>
    public IReadOnlyList<string> StoppedServerTraceIds => [.. _stopped];

    public void Dispose() => _listener.Dispose();

    /// <summary>
    /// Waits until <paramref name="condition"/> holds, giving other threads the processor between checks. The platform
    /// stops the span and records the duration of a request after the response has reached the client, so a test
    /// cannot read them the moment the response arrives. No time is slept: a spin that ends when the condition holds or
    /// after thirty seconds, so a missing signal fails the test instead of hanging it.
    /// </summary>
    public static async Task<bool> UntilAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(30))
            {
                return false;
            }

            await Task.Yield();
        }

        return true;
    }

    public BaseExportingMetricReader Reader { get; }

    /// <summary>The spans recorded so far.</summary>
    public IReadOnlyList<CapturedSpan> Spans => _spans.Items;

    internal BaseProcessor<Activity> Processor => _spans;

    /// <summary>Asks the meter provider to collect now, and returns every metric exported since the start.</summary>
    public IReadOnlyList<CapturedMetric> CollectMetrics()
    {
        Assert.True(Reader.Collect(10_000), "The metric reader did not collect.");
        return _exporter.Items;
    }

    private sealed class SpanProcessor : BaseProcessor<Activity>
    {
        private readonly ConcurrentQueue<CapturedSpan> _items = new();

        public IReadOnlyList<CapturedSpan> Items => [.. _items];

        public override void OnEnd(Activity data)
        {
            var tags = data.TagObjects.ToDictionary(
                tag => tag.Key,
                tag => Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty,
                StringComparer.Ordinal);
            List<string> texts = [data.DisplayName, data.OperationName, .. tags.Keys, .. tags.Values];
            foreach (var activityEvent in data.Events)
            {
                texts.Add(activityEvent.Name);
                texts.AddRange(activityEvent.Tags.Select(t => t.Key));
                texts.AddRange(activityEvent.Tags.Select(t => Convert.ToString(t.Value, CultureInfo.InvariantCulture) ?? string.Empty));
            }

            texts.AddRange(data.Baggage.Select(b => b.Key));
            texts.AddRange(data.Baggage.Select(b => b.Value ?? string.Empty));
            texts.Add(data.StatusDescription ?? string.Empty);
            var events = data.Events
                .Select(e => new CapturedEvent(
                    e.Name,
                    e.Tags.ToDictionary(t => t.Key, t => Convert.ToString(t.Value, CultureInfo.InvariantCulture) ?? string.Empty, StringComparer.Ordinal)))
                .ToList();
            _items.Enqueue(new CapturedSpan(
                data.Source.Name, data.DisplayName, data.TraceId.ToHexString(), data.Kind, tags, texts, data.Status, data.StatusDescription, events));
        }
    }

    private sealed class MetricExporter : BaseExporter<Metric>
    {
        private readonly ConcurrentQueue<CapturedMetric> _items = new();

        public IReadOnlyList<CapturedMetric> Items => [.. _items];

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                var points = new List<CapturedPoint>();
                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    var tags = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var tag in point.Tags)
                    {
                        tags[tag.Key] = Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                    }

                    var count = metric.MetricType switch
                    {
                        MetricType.Histogram or MetricType.ExponentialHistogram => point.GetHistogramCount(),
                        MetricType.LongSum or MetricType.LongSumNonMonotonic => point.GetSumLong(),
                        _ => 1,
                    };
                    points.Add(new CapturedPoint(tags, count));
                }

                _items.Enqueue(new CapturedMetric(metric.MeterName, metric.Name, points));
            }

            return ExportResult.Success;
        }
    }
}

internal static class TelemetryCaptureExtensions
{
    /// <summary>Adds the in-memory span processor and metric reader to the providers of this host only.</summary>
    public static TelemetryCapture CaptureTelemetry(this ApiHostFactory factory)
    {
        var capture = new TelemetryCapture();
        factory.WithServices(services =>
        {
            services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddProcessor(capture.Processor));
            services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddReader(capture.Reader));
        });
        return capture;
    }
}
