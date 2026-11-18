using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Web;
using MyApp.BuildingBlocks.Web.Telemetry;

namespace MyApp.Api.Tests.Telemetry;

// What the host's own OpenTelemetry providers record. The capture is added to the providers of one
// host through the SDK's base classes; activity sources and meters are process-wide, so each test finds its data by a
// trace id it sent or by a source or meter name that only it uses.
public class TracesAndMetricsTests
{
    private const string Version = SecurityRequests.Version;

    private const string ServerDurationMetric = "http.server.request.duration";

    private static string UniqueName(string area) => $"{LogCapture.ProjectName}.{area}.{Guid.NewGuid():N}";

    private static async Task<(HttpResponseMessage Response, string TraceId)> GetAsync(HttpClient client, string path)
    {
        var traceId = ActivityTraceId.CreateRandom().ToHexString();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom().ToHexString()}-01");
        return (await client.SendAsync(request, TestContext.Current.CancellationToken), traceId);
    }

    // Breaks when no server span is recorded for a request, it is not a server span, its route tag is missing, holds the
    // raw path instead of the template, or the span does not continue the incoming trace.
    [Theory]
    [InlineData(Version, Version)]
    [InlineData(ProblemFixtureEndpoint.Base + "/errors/Conflict", ProblemFixtureEndpoint.Base + "/errors/{kind}")]
    public async Task RequestRecordsAServerSpanTaggedWithTheRouteTemplate(string path, string route)
    {
        using var factory = new ApiHostFactory();
        using var capture = factory.CaptureTelemetry();
        using var client = factory.CreateClient();

        var (response, traceId) = await GetAsync(client, path);
        response.Dispose();

        await TelemetryCapture.UntilAsync(() => capture.Spans.Any(s => s.TraceId == traceId));
        var span = Assert.Single(capture.Spans, s => s.TraceId == traceId && s.Kind == ActivityKind.Server);
        Assert.Equal(route, span.Tags["http.route"]);
        Assert.Equal("GET", span.Tags["http.request.method"]);
    }

    // Breaks when the server duration histogram is not collected by the host's meter provider, or its measurements
    // are not tagged with the route template. Other tests' requests reach the provider too, so the check is that the
    // route is there, which the instrumentation only writes for the version route when it is subscribed.
    [Fact]
    public async Task ServerDurationHistogramIsCollectedByTheProviderWithTheRouteTag()
    {
        using var factory = new ApiHostFactory();
        using var capture = factory.CaptureTelemetry();
        using var client = factory.CreateClient();

        var (response, _) = await GetAsync(client, Version);
        response.Dispose();

        var collected = await TelemetryCapture.UntilAsync(() => capture.CollectMetrics()
            .Where(m => m.Name == ServerDurationMetric)
            .SelectMany(m => m.Points)
            .Any(p => p.Tags.TryGetValue("http.route", out var route) && route == Version && p.Count >= 1));
        Assert.True(collected, "The provider collected no measurement of the server duration tagged with the route template.");
    }

    // Breaks when the platform's measurement of the request is not tagged with the route template. The meter of the
    // host is told apart from the meters of other hosts by its scope, the host's own meter factory.
    [Fact]
    public async Task ServerDurationMeasurementOfTheRequestIsTaggedWithTheRouteTemplate()
    {
        using var factory = new ApiHostFactory();
        using var client = factory.CreateClient();
        var meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        var routes = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Name == ServerDurationMetric && ReferenceEquals(instrument.Meter.Scope, meterFactory))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "http.route")
                {
                    routes.Enqueue(tag.Value?.ToString());
                }
            }
        });
        listener.Start();

        var (response, _) = await GetAsync(client, Version);
        response.Dispose();

        await TelemetryCapture.UntilAsync(() => !routes.IsEmpty);
        Assert.Equal([Version], routes);
    }

    // Breaks when a health probe is traced (the filter is missing, applies to one probe only, or is case or trailing
    // slash sensitive), or when tracing stops altogether (the version request after the probes has its span).
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/health/live/")]
    [InlineData("/HEALTH/READY")]
    public async Task HealthProbeRecordsNoSpan(string path)
    {
        using var factory = new ApiHostFactory();
        using var capture = factory.CaptureTelemetry();
        using var client = factory.CreateClient();

        var (probe, probeTrace) = await GetAsync(client, path);
        var (version, versionTrace) = await GetAsync(client, Version);
        probe.Dispose();
        version.Dispose();

        // Both requests are over (their activities stopped) before the spans are read.
        Assert.True(await TelemetryCapture.UntilAsync(() => capture.StoppedServerTraceIds.Contains(probeTrace) && capture.StoppedServerTraceIds.Contains(versionTrace)));
        Assert.True(await TelemetryCapture.UntilAsync(() => capture.Spans.Any(s => s.TraceId == versionTrace && s.Kind == ActivityKind.Server)));
        Assert.DoesNotContain(capture.Spans, s => s.TraceId == probeTrace);
    }

    // Breaks when a header value, the query string or the body of a request is recorded as a tag, event or name of a
    // server span, for any kind of request. Each request has a trace id of its own, so the spans are counted as proof.
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task NoServerSpanHoldsAHeaderQueryOrBodyValue(string environment)
    {
        using var factory = SecretScenarios.CreateFactory(environment, null);
        using var capture = factory.CaptureTelemetry();
        using var client = factory.CreateClient();

        var run = await SecretScenarios.RunAsync(client);

        await TelemetryCapture.UntilAsync(() => capture.Spans.Count(s => run.TraceIds.Contains(s.TraceId) && s.Kind == ActivityKind.Server) >= run.Requests);
        var mine = capture.Spans.Where(s => run.TraceIds.Contains(s.TraceId)).ToList();
        Assert.Equal(run.Requests, mine.Count(s => s.Kind == ActivityKind.Server));
        foreach (var span in capture.Spans)
        {
            foreach (var text in span.Texts)
            {
                foreach (var secret in run.Secrets.Values)
                {
                    Assert.False(text.Contains(secret, StringComparison.OrdinalIgnoreCase), $"A span holds a request value ('{secret}'): {span.Name} -> {text}");
                }

                Assert.DoesNotContain(ProblemFixtureEndpoint.Secret, text, StringComparison.Ordinal);
            }
        }
    }

    // Breaks when the user agent, the query string or any header leaks into the tags of the server span of a request
    // that sends them: the span of the version request has no such tag at all.
    [Fact]
    public async Task ServerSpanHasNoQueryOrUserAgentTag()
    {
        using var factory = new ApiHostFactory();
        using var capture = factory.CaptureTelemetry();
        using var client = factory.CreateClient();
        var secrets = RequestSecrets.Create();
        using var request = secrets.Request(HttpMethod.Get, Version);
        var traceId = ActivityTraceId.CreateRandom().ToHexString();
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom().ToHexString()}-01");
        request.Headers.TryAddWithoutValidation("User-Agent", secrets.CustomHeader);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await TelemetryCapture.UntilAsync(() => capture.Spans.Any(s => s.TraceId == traceId && s.Kind == ActivityKind.Server));
        var span = Assert.Single(capture.Spans, s => s.TraceId == traceId && s.Kind == ActivityKind.Server);
        Assert.DoesNotContain(span.Tags.Keys, key => key.StartsWith("http.request.header.", StringComparison.Ordinal));
        Assert.False(span.Tags.TryGetValue("url.query", out var query), $"The span has a url.query tag: {query}");
        Assert.False(span.Tags.TryGetValue("user_agent.original", out var agent), $"The span has a user_agent.original tag: {agent}");
    }

    // Breaks when the client span of an outgoing HttpClient call keeps the user info, the query string or a header of
    // its target, or when no client span is recorded at all (the README promises one with the target's path).
    [Fact]
    public async Task ClientSpanKeepsTheTargetPathButNoUserInfoQueryOrHeaderValue()
    {
        using var factory = new ApiHostFactory().WithServices(services => services.AddSingleton<IEndpoint, OutgoingCallEndpoint>());
        using var capture = factory.CaptureTelemetry();
        using var client = factory.CreateClient();
        var secrets = RequestSecrets.Create();
        using var request = secrets.Request(HttpMethod.Get, OutgoingCallEndpoint.Route);
        var traceId = ActivityTraceId.CreateRandom().ToHexString();
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom().ToHexString()}-01");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await TelemetryCapture.UntilAsync(() => capture.Spans.Any(s => s.TraceId == traceId && s.Kind == ActivityKind.Server));
        var spans = capture.Spans.Where(s => s.TraceId == traceId).ToList();
        var clientSpan = Assert.Single(spans, s => s.Kind == ActivityKind.Client);
        Assert.Contains(OutgoingCallEndpoint.TargetHost, string.Join(' ', clientSpan.Tags.Values), StringComparison.Ordinal);
        Assert.Contains(OutgoingCallEndpoint.TargetPath, string.Join(' ', clientSpan.Tags.Values), StringComparison.Ordinal);
        foreach (var span in spans)
        {
            foreach (var text in span.Texts)
            {
                foreach (var secret in secrets.Values.Concat(OutgoingCallEndpoint.Secrets))
                {
                    Assert.False(text.Contains(secret, StringComparison.OrdinalIgnoreCase), $"A {span.Kind} span holds a secret ('{secret}'): {span.Name} -> {text}");
                }
            }
        }
    }

    // Breaks when a source added with AddTelemetrySource is not collected: the span would never reach the provider.
    [Fact]
    public void SourceAddedWithAddTelemetrySourceIsCollected()
    {
        var name = $"Vendor.Library.{Guid.NewGuid():N}";
        using var factory = new ApiHostFactory().WithServices(services => services.AddTelemetrySource(name));
        using var capture = factory.CaptureTelemetry();
        using var client = factory.CreateClient();
        using var source = new ActivitySource(name);

        using (var activity = source.StartActivity("query"))
        {
            Assert.NotNull(activity);
            Assert.True(activity.Recorded);
        }

        Assert.Contains(capture.Spans, s => s.Source == name && s.Name == "query");
    }

    // Breaks when a name that ends with a wildcard does not match the sources it stands for.
    [Fact]
    public void SourceAddedWithAWildcardCollectsEverySourceThatStartsWithTheName()
    {
        var prefix = $"Vendor{Guid.NewGuid():N}";
        using var factory = new ApiHostFactory().WithServices(services => services.AddTelemetrySource(prefix + ".*"));
        using var capture = factory.CaptureTelemetry();
        using var client = factory.CreateClient();
        using var source = new ActivitySource(prefix + ".Database");

        using (var activity = source.StartActivity("query"))
        {
            Assert.NotNull(activity);
        }

        Assert.Contains(capture.Spans, s => s.Source == prefix + ".Database");
    }

    // Breaks when the provider collects every source (including those nobody added), which would trace third-party
    // libraries that attach request data to their spans.
    [Fact]
    public void SourceThatWasNotAddedIsNotRecorded()
    {
        var name = $"Vendor.NotAdded.{Guid.NewGuid():N}";
        using var factory = new ApiHostFactory();
        using var capture = factory.CaptureTelemetry();
        using var client = factory.CreateClient();
        using var source = new ActivitySource(name);

        using (var activity = source.StartActivity("query"))
        {
            Assert.Null(activity);
        }

        Assert.DoesNotContain(capture.Spans, s => s.Source == name);
    }

    // Breaks when the sources of the application stop being collected without registration, or when the pattern is
    // wider or narrower than the README says: the project name followed by a dot.
    [Fact]
    public void SourceNamedAfterTheProjectIsCollectedWithoutRegistrationAndOneThatOnlyStartsLikeItIsNot()
    {
        var automatic = UniqueName("Sales");
        var lookalike = $"{LogCapture.ProjectName}Other.{Guid.NewGuid():N}";
        using var factory = new ApiHostFactory();
        using var capture = factory.CaptureTelemetry();
        using var client = factory.CreateClient();
        using var automaticSource = new ActivitySource(automatic);
        using var lookalikeSource = new ActivitySource(lookalike);

        using (var activity = automaticSource.StartActivity("place-order"))
        {
            Assert.NotNull(activity);
            Assert.True(activity.Recorded);
        }

        using (var activity = lookalikeSource.StartActivity("place-order"))
        {
            Assert.Null(activity);
        }

        Assert.Contains(capture.Spans, s => s.Source == automatic);
        Assert.DoesNotContain(capture.Spans, s => s.Source == lookalike);
    }

    // Breaks when a meter added with AddTelemetryMeter is not collected, one that was not added is, or a meter named
    // after the project is not collected without registration. The measurements are read from the host's meter provider.
    [Fact]
    public void MeterIsCollectedWhenAddedOrNamedAfterTheProjectAndNotOtherwise()
    {
        var added = $"Vendor.Library.{Guid.NewGuid():N}";
        var automatic = UniqueName("Sales");
        var notAdded = $"Vendor.NotAdded.{Guid.NewGuid():N}";
        using var factory = new ApiHostFactory().WithServices(services => services.AddTelemetryMeter(added));
        using var capture = factory.CaptureTelemetry();
        using var client = factory.CreateClient();
        using var addedMeter = new Meter(added);
        using var automaticMeter = new Meter(automatic);
        using var notAddedMeter = new Meter(notAdded);

        addedMeter.CreateCounter<long>("queries").Add(3);
        automaticMeter.CreateCounter<long>("orders").Add(2);
        notAddedMeter.CreateCounter<long>("hidden").Add(1);

        var metrics = capture.CollectMetrics();
        var queries = metrics.First(m => m.Meter == added && m.Name == "queries");
        Assert.Equal(3, Assert.Single(queries.Points).Count);
        var orders = metrics.First(m => m.Meter == automatic && m.Name == "orders");
        Assert.Equal(2, Assert.Single(orders.Points).Count);
        Assert.DoesNotContain(metrics, m => m.Meter == notAdded);
    }

    // Breaks when a blank name is accepted and silently subscribes to nothing, or to everything.
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void BlankSourceOrMeterNameIsRejected(string name)
    {
        var services = new ServiceCollection();

        Assert.ThrowsAny<ArgumentException>(() => services.AddTelemetrySource(name));
        Assert.ThrowsAny<ArgumentException>(() => services.AddTelemetryMeter(name));
    }
}
