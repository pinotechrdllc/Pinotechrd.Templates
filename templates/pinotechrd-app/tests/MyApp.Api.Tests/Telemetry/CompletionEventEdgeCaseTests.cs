using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Web;
using MyApp.BuildingBlocks.Web.Telemetry;
using Serilog.Events;

namespace MyApp.Api.Tests.Telemetry;

// A failure after the response has started, and the mechanics that the rule on routes relies on.
public class CompletionEventEdgeCaseTests
{
    private static ApiHostFactory WithFixture(ApiHostFactory factory) =>
        factory.WithServices(services => services.AddSingleton<IEndpoint, TelemetryFixtureEndpoint>());

    // Breaks when an exception after the first flush is logged twice or three times (handler, completion middleware,
    // server), is attached to the completion event, or the completion event reports 500 instead of the status sent.
    // The in-memory server differs from Kestrel here: the client sees the abort as an exception (or a truncated body)
    // instead of a reset connection, so only what the application logged is asserted about the outcome.
    [Fact]
    public async Task ExceptionAfterTheResponseStartedIsLoggedOnceAndTheCompletionEventReportsTheStatusSent()
    {
        using var factory = WithFixture(new ApiHostFactory());
        var sink = factory.CaptureLogs();
        using var client = factory.CreateClient();

        // Either the headers arrive and reading the body fails, or the call fails: both are the abort.
        _ = await Record.ExceptionAsync(async () =>
        {
            using var response = await client.GetAsync(TelemetryFixtureEndpoint.MidResponseFailure, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            _ = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        });

        var thrown = Assert.Single(sink.Events, e => e.Exception is not null);
        Assert.IsType<InvalidOperationException>(thrown.Exception);
        Assert.Equal(LogEventLevel.Error, thrown.Level);
        var completion = Assert.Single(sink.CompletionEvents());
        Assert.Equal(200, completion.ScalarOf(LogCapture.StatusProperty));
        Assert.Equal(LogEventLevel.Error, completion.Level);
        Assert.Null(completion.Exception);
        Assert.Equal(TelemetryFixtureEndpoint.MidResponseFailure, completion.ScalarOf(LogCapture.PathProperty));
    }

    // Breaks when the completion event of a request that fails before the response starts changes with the new
    // handling: it still reports 500 and is at Error, with the exception logged by the handler once.
    [Fact]
    public async Task ExceptionBeforeTheResponseStartedStillCompletesAt500AndIsLoggedOnce()
    {
        using var factory = new ApiHostFactory();
        var sink = factory.CaptureLogs();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(SecurityRequests.Throwing, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Single(sink.Events, e => e.Exception is not null);
        var completion = Assert.Single(sink.CompletionEvents());
        Assert.Equal(500, completion.ScalarOf(LogCapture.StatusProperty));
        Assert.Null(completion.Exception);
    }

    // Breaks when the raw path of the event includes the query string, or the route holds the id instead of the
    // template with its parameter name: the rule that routes carry opaque ids relies on both.
    [Fact]
    public async Task EventHasTheRawPathWithoutQueryAndTheTemplateWithTheParameterName()
    {
        using var factory = WithFixture(new ApiHostFactory());
        var sink = factory.CaptureLogs();
        using var client = factory.CreateClient();
        var id = Guid.NewGuid().ToString("D");

        using var response = await client.GetAsync(TelemetryFixtureEndpoint.OrderPath(id) + "?token=s3cr3t-query-value&page=2", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var completion = Assert.Single(sink.CompletionEvents());
        Assert.Equal(TelemetryFixtureEndpoint.OrderPath(id), completion.ScalarOf(LogCapture.PathProperty));
        Assert.Equal(TelemetryFixtureEndpoint.OrderTemplate, completion.ScalarOf(RequestLogging.RouteProperty));
        Assert.DoesNotContain("s3cr3t", completion.RenderMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}
