using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Web.Telemetry;
using Serilog.Events;

namespace MyApp.Api.Tests.Telemetry;

// The one completion event of each request. Every test uses its
// own host with its own sink, so tests running in parallel never see each other's events.
public class RequestCompletionEventTests
{
    private const string Version = SecurityRequests.Version;

    private const string FailureError = ProblemFixtureEndpoint.Base + "/errors/Failure";

    private const string ConflictError = ProblemFixtureEndpoint.Base + "/errors/Conflict";

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string? traceparent = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (traceparent is not null)
        {
            request.Headers.TryAddWithoutValidation("traceparent", traceparent);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static (string TraceId, string Header) NewTraceparent()
    {
        var traceId = ActivityTraceId.CreateRandom().ToHexString();
        return (traceId, $"00-{traceId}-{ActivitySpanId.CreateRandom().ToHexString()}-01");
    }

    // Breaks when a request writes no completion event, writes it twice (a second middleware, or the event repeated by
    // the error path), or leaves out the method, raw path, route template, status code, elapsed time or trace id.
    [Fact]
    public async Task VersionRequestWritesOneCompletionEventWithTheDocumentedProperties()
    {
        using var factory = new ApiHostFactory();
        var sink = factory.CaptureLogs();
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, Version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var completion = Assert.Single(sink.CompletionEvents());
        Assert.Equal("GET", completion.ScalarOf(LogCapture.MethodProperty));
        Assert.Equal(Version, completion.ScalarOf(LogCapture.PathProperty));
        Assert.Equal(Version, completion.ScalarOf(RequestLogging.RouteProperty));
        Assert.Equal(200, completion.ScalarOf(LogCapture.StatusProperty));
        var elapsed = Assert.IsType<double>(completion.ScalarOf(LogCapture.ElapsedProperty));
        Assert.True(elapsed >= 0, "Elapsed must be a time in milliseconds.");
        Assert.Matches("^[0-9a-f]{32}$", completion.TraceIdOf());
        Assert.Contains("GET", completion.RenderMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.Contains(Version, completion.RenderMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.Contains("200", completion.RenderMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    // Breaks when the path of the event is the route template instead of the raw path (the two differ for a route
    // with a parameter), or the route property carries the raw path.
    [Fact]
    public async Task EventHasTheRawPathAndTheRouteTemplateOfAParameterizedRoute()
    {
        using var factory = new ApiHostFactory();
        var sink = factory.CaptureLogs();
        using var client = factory.CreateClient();
        var rawPath = ConflictError;

        using var response = await GetAsync(client, rawPath);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var completion = Assert.Single(sink.CompletionEvents());
        Assert.Equal(rawPath, completion.ScalarOf(LogCapture.PathProperty));
        Assert.Equal(ProblemFixtureEndpoint.Base + "/errors/{kind}", completion.ScalarOf(RequestLogging.RouteProperty));
    }

    // Breaks when the level does not follow the outcome (a 4xx raised to Warning, a 5xx left at Information), when a
    // route is invented for an unmatched request, or when a request that is answered through ToProblem() is not logged.
    [Theory]
    [InlineData(Version, 200, LogEventLevel.Information, Version)]
    [InlineData(SecurityRequests.Unknown, 404, LogEventLevel.Information, null)]
    [InlineData(FailureError, 422, LogEventLevel.Information, ProblemFixtureEndpoint.Base + "/errors/{kind}")]
    [InlineData(ConflictError, 409, LogEventLevel.Information, ProblemFixtureEndpoint.Base + "/errors/{kind}")]
    [InlineData(SecurityRequests.Throwing, 500, LogEventLevel.Error, SecurityRequests.Throwing)]
    public async Task CompletionEventHasTheStatusRouteAndLevelOfTheOutcome(string path, int status, LogEventLevel level, string? route)
    {
        using var factory = new ApiHostFactory();
        var sink = factory.CaptureLogs();
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, path);

        Assert.Equal(status, (int)response.StatusCode);
        var completion = Assert.Single(sink.CompletionEvents());
        Assert.Equal("GET", completion.ScalarOf(LogCapture.MethodProperty));
        Assert.Equal(path, completion.ScalarOf(LogCapture.PathProperty));
        Assert.Equal(status, completion.ScalarOf(LogCapture.StatusProperty));
        Assert.Equal(level, completion.Level);
        Assert.Equal(route, completion.ScalarOf(RequestLogging.RouteProperty));
        Assert.IsType<double>(completion.ScalarOf(LogCapture.ElapsedProperty));
        Assert.NotNull(completion.TraceIdOf());
    }

    // Breaks when the event does not carry the trace id the client is shown in the problem response: an error that a
    // client reports could then not be found in the logs.
    [Theory]
    [InlineData(SecurityRequests.Unknown)]
    [InlineData(SecurityRequests.Throwing)]
    public async Task EventTraceIdEqualsTheTraceIdOfTheProblemResponse(string path)
    {
        using var factory = new ApiHostFactory();
        var sink = factory.CaptureLogs();
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, path);

        var body = await ProblemAssert.IsProblemAsync(response, (int)response.StatusCode, path == SecurityRequests.Throwing ? "internal_error" : "not_found");
        var completion = Assert.Single(sink.CompletionEvents());
        Assert.Equal(body.GetProperty("traceId").GetString(), completion.TraceIdOf());
    }

    // Breaks when the event starts its own trace instead of continuing the one of an incoming W3C traceparent, for a
    // success, an unmatched route and a failure alike.
    [Theory]
    [InlineData(Version)]
    [InlineData(SecurityRequests.Unknown)]
    [InlineData(SecurityRequests.Throwing)]
    public async Task EventTraceIdEqualsTheTraceIdOfAnIncomingTraceparent(string path)
    {
        using var factory = new ApiHostFactory();
        var sink = factory.CaptureLogs();
        using var client = factory.CreateClient();
        var (traceId, header) = NewTraceparent();

        using var response = await GetAsync(client, path, header);

        var completion = Assert.Single(sink.CompletionEvents());
        Assert.Equal(traceId, completion.TraceIdOf());
        if (!response.IsSuccessStatusCode)
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(traceId, document.RootElement.GetProperty("traceId").GetString());
        }
    }

    // Breaks when an unhandled exception is not logged, is logged twice (the handler and the completion middleware both
    // writing it), has no exception attached, is not at Error, or its request is not completed at Error with the same trace.
    [Fact]
    public async Task UnhandledExceptionIsLoggedOnceWithTheExceptionAndTheRequestCompletesAtError()
    {
        using var factory = new ApiHostFactory();
        var sink = factory.CaptureLogs();
        using var client = factory.CreateClient();
        var (traceId, header) = NewTraceparent();

        using var response = await GetAsync(client, SecurityRequests.Throwing, header);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var withException = Assert.Single(sink.Events, e => e.Exception is not null);
        Assert.Equal(LogEventLevel.Error, withException.Level);
        Assert.IsType<InvalidOperationException>(withException.Exception);
        Assert.Equal(ProblemFixtureEndpoint.Secret, withException.Exception.Message);
        Assert.Equal(traceId, withException.TraceIdOf());
        var completion = Assert.Single(sink.CompletionEvents());
        Assert.Equal(LogEventLevel.Error, completion.Level);
        Assert.Equal(traceId, completion.TraceIdOf());
        Assert.Equal(500, completion.ScalarOf(LogCapture.StatusProperty));
        Assert.Equal(SecurityRequests.Throwing, completion.ScalarOf(RequestLogging.RouteProperty));
        Assert.DoesNotContain(sink.Events, e => e.Level >= LogEventLevel.Warning && !ReferenceEquals(e, withException) && !ReferenceEquals(e, completion));
    }

    // Breaks when a health probe that succeeds writes an event at Information or above (polling every few seconds
    // would flood the logs), or when the probe stops answering.
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task SuccessfulHealthProbeWritesNoEventAtInformationOrAbove(string path)
    {
        using var factory = new ApiHostFactory();
        var sink = factory.CaptureLogs();
        using var client = factory.CreateClient();

        using var probe = await GetAsync(client, path);
        using var version = await GetAsync(client, Version);

        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
        Assert.DoesNotContain(sink.Events, e => e.Level >= LogEventLevel.Information && Equals(e.ScalarOf(LogCapture.PathProperty), path));
        // The version request after it is still logged, so the sink works and the probe was skipped on purpose.
        Assert.Single(sink.CompletionEvents(), e => Equals(e.ScalarOf(LogCapture.PathProperty), Version));
    }

    // Breaks when the probe is not logged at all instead of at the Verbose level the README documents: the previous
    // test would then pass for the wrong reason. With the default level at Trace the event exists, below Information.
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthProbeCompletionEventIsWrittenAtVerboseWhenTraceIsEnabled(string path)
    {
        using var factory = new ApiHostFactory().WithEarlySetting("Logging:LogLevel:Default", "Trace");
        var sink = factory.CaptureLogs();
        using var client = factory.CreateClient();

        using var probe = await GetAsync(client, path);

        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
        var completion = Assert.Single(sink.CompletionEvents(), e => Equals(e.ScalarOf(LogCapture.PathProperty), path));
        Assert.Equal(LogEventLevel.Verbose, completion.Level);
    }

    // Breaks when an event leaves out the application name, environment or version, or when they hold anything but
    // the name of the application assembly, the environment the host runs in and the assembly's version. Checked on
    // every event the host wrote, including the ones that are not request events.
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task EveryEventCarriesTheApplicationNameEnvironmentAndVersion(string environment)
    {
        using var factory = ApiHostFactory.ForEnvironment(environment).WithEarlySetting("Logging:LogLevel:Default", "Debug");
        var sink = factory.CaptureLogs();
        using var client = factory.CreateClient();
        using var ok = await GetAsync(client, Version);
        using var failure = await GetAsync(client, SecurityRequests.Throwing);

        var assembly = typeof(Program).Assembly;
        var expectedVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString();
        Assert.NotEmpty(sink.Events);
        foreach (var logEvent in sink.Events)
        {
            Assert.Equal(assembly.GetName().Name, logEvent.ScalarOf(TelemetryRegistration.ApplicationProperty));
            Assert.Equal(environment, logEvent.ScalarOf(TelemetryRegistration.EnvironmentProperty));
            Assert.Equal(expectedVersion, logEvent.ScalarOf(TelemetryRegistration.VersionProperty));
        }
    }
}
