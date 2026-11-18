using System.Net;
using System.Net.Sockets;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Web.Telemetry;
using Serilog.Events;

namespace MyApp.Api.Tests.Telemetry;

// Criterion 12: logging and telemetry cost nothing when no collector is configured, and do not hurt requests when the
// configured one is unreachable.
public class HostLifetimeTelemetryTests
{
    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path) =>
        await client.GetAsync(path, TestContext.Current.CancellationToken);

    /// <summary>A local port that nothing listens on: it was free a moment ago and its listener is gone.</summary>
    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    // Breaks when a host with no OTLP endpoint warns or fails while it starts, serves a request or stops, for example
    // by exporting to the default collector address that nobody runs, or by an exporter that is registered regardless.
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task HostWithNoOtlpEndpointStartsServesAndStopsWithoutAWarning(string environment)
    {
        var factory = ApiHostFactory.ForEnvironment(environment);
        var sink = factory.CaptureLogs();
        var client = factory.CreateClient();

        using (var ok = await GetAsync(client, SecurityRequests.Version))
        using (var notFound = await GetAsync(client, SecurityRequests.Unknown))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        }

        client.Dispose();
        var stop = await Record.ExceptionAsync(async () => await factory.DisposeAsync());

        Assert.Null(stop);
        Assert.NotEmpty(sink.CompletionEvents());
        Assert.DoesNotContain(sink.Events, e => e.Level >= LogEventLevel.Warning);
    }

    // Breaks when an unreachable collector makes a request fail, throws from the host or logs a warning or an error.
    // The port is closed, so the connection is refused at once and the test does not wait for a timeout.
    [Fact]
    public async Task RequestsSucceedAndNothingIsLoggedWhenTheConfiguredCollectorIsUnreachable()
    {
        var factory = new ApiHostFactory()
            .WithEarlySetting(TelemetryRegistration.OtlpEndpointKey, $"http://127.0.0.1:{ClosedPort()}")
            .WithEarlySetting("OTEL_EXPORTER_OTLP_TIMEOUT", "500");
        var sink = factory.CaptureLogs();
        var client = factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            using var response = await GetAsync(client, SecurityRequests.Version);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var failure = await GetAsync(client, SecurityRequests.Throwing))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        }

        client.Dispose();
        var stop = await Record.ExceptionAsync(async () => await factory.DisposeAsync());

        Assert.Null(stop);
        // The only event at Error is the unhandled exception that the test caused, and its request.
        Assert.DoesNotContain(sink.Events, e => e.Level >= LogEventLevel.Warning && !(e.Exception is InvalidOperationException) && !LogCapture.IsCompletion(e));
    }
}
