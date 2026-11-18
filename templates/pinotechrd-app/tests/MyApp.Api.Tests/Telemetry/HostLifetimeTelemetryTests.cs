using System.Net;
using System.Net.Sockets;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Web.Telemetry;
using Serilog.Events;

namespace MyApp.Api.Tests.Telemetry;

// Logging and telemetry cost nothing when no collector is configured, and do not hurt requests when the
// configured one is unreachable.
public class HostLifetimeTelemetryTests
{
    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path) =>
        await client.GetAsync(path, TestContext.Current.CancellationToken);

    /// <summary>A local port that nothing listens on: it was free a moment ago and its listener is gone.</summary>
    // A port that nothing listens on and that nothing else can take while the test runs: a socket that is bound and never
    // listens keeps the number reserved, and connections to it are refused. (A port chosen and then released could be
    // given to another test's listener, or to another process, before the host connects.)
    private sealed class ClosedPort : IDisposable
    {
        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        public ClosedPort() => _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        public int Number => ((IPEndPoint)_socket.LocalEndPoint!).Port;

        public void Dispose() => _socket.Dispose();
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
        using var closed = new ClosedPort();
        var factory = new ApiHostFactory()
            .WithEarlySetting(TelemetryRegistration.OtlpEndpointKey, $"http://127.0.0.1:{closed.Number}")
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
