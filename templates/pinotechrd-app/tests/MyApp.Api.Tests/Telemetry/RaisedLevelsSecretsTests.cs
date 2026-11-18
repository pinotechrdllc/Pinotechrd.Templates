using System.Diagnostics;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Telemetry;

// Criterion 17: the whole secrets matrix also holds with the framework's parent categories raised to Trace, and the
// client address (the remote address of the request) is in no event and in no span tag.
public class RaisedLevelsSecretsTests
{
    // Breaks when any event or span, at any level an operator can raise, holds a header value, query, body or the
    // client address: an unpinned framework category that writes one, or a pinned one that is no longer pinned.
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task NoEventOrSpanHoldsARequestValueOrTheClientAddressWithParentCategoriesAtTrace(string environment)
    {
        using var factory = SecretScenarios.CreateFactory(environment, "Trace")
            .WithEarlySetting("Logging:LogLevel:Microsoft.AspNetCore", "Trace")
            .WithEarlySetting("Logging:LogLevel:Microsoft", "Trace");
        var sink = factory.CaptureLogs();
        using var capture = factory.CaptureTelemetry();
        using var client = factory.CreateClient();

        var run = await SecretScenarios.RunAsync(client);

        Assert.Equal(run.Requests, sink.CompletionEvents().Count);
        var unhandled = Assert.Single(sink.Events, e => e.Exception is not null && e.Exception.ToString().Contains(ProblemFixtureEndpoint.Secret, StringComparison.Ordinal));
        var forbiddenValues = run.Secrets.Values.Concat(SecretScenarios.RemoteAddresses.Distinct()).ToList();
        foreach (var logEvent in sink.Events)
        {
            foreach (var text in logEvent.TextsOf())
            {
                foreach (var forbidden in forbiddenValues)
                {
                    Assert.False(
                        text.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                        $"A log event holds '{forbidden}': [{logEvent.Level}] {logEvent.CategoryOf()} -> {text}");
                }
            }

            var withoutAccepted = ReferenceEquals(logEvent, unhandled) ? logEvent.TextsWithoutException() : logEvent.TextsOf();
            Assert.DoesNotContain(withoutAccepted, t => t.Contains(ProblemFixtureEndpoint.Secret, StringComparison.Ordinal));
        }

        await TelemetryCapture.UntilAsync(() => capture.Spans.Count(s => run.TraceIds.Contains(s.TraceId) && s.Kind == ActivityKind.Server) >= run.Requests);
        var spans = capture.Spans.Where(s => run.TraceIds.Contains(s.TraceId)).ToList();
        Assert.Equal(run.Requests, spans.Count(s => s.Kind == ActivityKind.Server));
        foreach (var span in spans)
        {
            foreach (var text in span.Texts)
            {
                foreach (var forbidden in forbiddenValues)
                {
                    Assert.False(text.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"A span holds '{forbidden}': {span.Name} -> {text}");
                }
            }
        }
    }
}
