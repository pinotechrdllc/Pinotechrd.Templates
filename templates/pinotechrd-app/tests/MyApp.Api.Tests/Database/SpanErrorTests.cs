using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Infrastructure.Database;
using Npgsql;

namespace MyApp.Api.Tests.Database;

// Criterion 19: no span holds exception text, whichever source recorded the failure. Needs Docker for the failing
// command; the other two cases need none but share the helpers.
[Collection(PostgresContainer.Name)]
public class SpanErrorTests(PostgresFixture postgres)
{
    private const string InputValue = "not-a-uuid-input-value-9921";
    private const string HostName = "unresolvable-host-4471.invalid";

    private static IEnumerable<string> EventAndStatusTexts(CapturedSpan span) =>
        span.Events.SelectMany(e => e.Attributes.Keys.Concat(e.Attributes.Values).Append(e.Name)).Append(span.StatusDescription ?? string.Empty);

    private static void AssertExceptionEventHasTypeOnly(CapturedSpan span)
    {
        foreach (var exceptionEvent in span.Events.Where(e => e.Name == "exception"))
        {
            Assert.False(string.IsNullOrEmpty(exceptionEvent.Attributes.GetValueOrDefault("exception.type")), "The exception event lost its type.");
            Assert.True(string.IsNullOrEmpty(exceptionEvent.Attributes.GetValueOrDefault("exception.message")));
            Assert.True(string.IsNullOrEmpty(exceptionEvent.Attributes.GetValueOrDefault("exception.stacktrace")));
        }
    }

    // Breaks when a failed command's span keeps the exception text (an input value quoted by the server's message) in
    // any tag, event attribute or status description, loses its error status or error.type, or stops redacting the
    // exception event. The control shows the exception itself does quote the value.
    [Fact]
    public async Task FailedCommandRecordsAnErrorSpanWithTheErrorTypeAndNoExceptionText()
    {
        var marker = "failing_" + Guid.NewGuid().ToString("N");
        using var factory = new ApiHostFactory().WithDatabase(await postgres.CreateDatabaseAsync());
        using var capture = factory.CaptureTelemetry();
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();

        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = dataSource.CreateCommand($"SELECT $1::uuid AS {marker}");
            command.Parameters.AddWithValue(InputValue);
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        });

        Assert.Contains(InputValue, exception.Message, StringComparison.Ordinal);
        Assert.True(await TelemetryCapture.UntilAsync(() => capture.Spans.Any(s => s.Source == DatabaseRegistration.TelemetryName && s.Texts.Any(t => t.Contains(marker, StringComparison.Ordinal)))));
        var span = Assert.Single(capture.Spans, s => s.Source == DatabaseRegistration.TelemetryName && s.Texts.Any(t => t.Contains(marker, StringComparison.Ordinal)));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(exception.SqlState, span.Tags["error.type"]);
        Assert.Equal(span.Tags["error.type"], span.StatusDescription);
        foreach (var any in capture.Spans)
        {
            Assert.DoesNotContain(any.Texts, t => t.Contains(InputValue, StringComparison.Ordinal));
        }

        // The platform cannot remove the event: it stays, with its type and empty message and stack trace.
        var recorded = Assert.Single(span.Events, e => e.Name == "exception");
        Assert.Equal(typeof(PostgresException).FullName, recorded.Attributes["exception.type"]);
        AssertExceptionEventHasTypeOnly(span);
    }

    // Breaks when a connection failure to a named host puts the host name in a status description or an event
    // attribute of any span. Npgsql records no span for an open that fails, so this holds today with no span to inspect; it
    // protects the day a span or an exception event is recorded for it.
    [Fact]
    public async Task ConnectionFailureToANamedHostPutsTheHostNameInNoStatusOrEventAttribute()
    {
        var connection = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync()) { Host = HostName, Timeout = 5 };
        using var factory = new ApiHostFactory().WithDatabase(connection.ConnectionString);
        using var capture = factory.CaptureTelemetry();
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var command = dataSource.CreateCommand("SELECT 1 AS connection_failure");
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        });

        Assert.IsAssignableFrom<NpgsqlException>(exception);
        foreach (var span in capture.Spans)
        {
            Assert.DoesNotContain(EventAndStatusTexts(span), t => t.Contains(HostName, StringComparison.Ordinal));
            AssertExceptionEventHasTypeOnly(span);
        }
    }

    // Breaks when a failing server span keeps the message or the stack trace of the exception that the endpoint threw.
    [Fact]
    public async Task FailingServerSpanCarriesNoExceptionMessageOrStackTrace()
    {
        using var factory = new ApiHostFactory();
        using var capture = factory.CaptureTelemetry();
        using var client = factory.CreateClient();
        var traceId = ActivityTraceId.CreateRandom().ToHexString();
        using var request = new HttpRequestMessage(HttpMethod.Get, ProblemFixtureEndpoint.Base + "/throw");
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom().ToHexString()}-01");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(await TelemetryCapture.UntilAsync(() => capture.Spans.Any(s => s.TraceId == traceId && s.Kind == ActivityKind.Server)));
        var span = Assert.Single(capture.Spans, s => s.TraceId == traceId && s.Kind == ActivityKind.Server);
        Assert.DoesNotContain(span.Texts, t => t.Contains(ProblemFixtureEndpoint.Secret, StringComparison.Ordinal));
        Assert.DoesNotContain(span.Texts, t => t.Contains("ProblemFixtureEndpoint", StringComparison.Ordinal));
        // The exception handler of the pipeline answers the request, so the server span has no exception event at all;
        // if a future pipeline records one, it must keep the type only.
        AssertExceptionEventHasTypeOnly(span);
    }
}
