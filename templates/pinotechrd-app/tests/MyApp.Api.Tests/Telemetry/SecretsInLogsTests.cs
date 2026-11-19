using System.Globalization;
using MyApp.Api.Tests.Support;
using Serilog.Events;

namespace MyApp.Api.Tests.Telemetry;

// No header value, query string or body reaches a log event, whatever the outcome of the request and
// whatever the log level. Every request carries an Authorization header, a Cookie header, a custom header, a query
// string with a token and a JSON body with a recognizable value (see SecretScenarios); the sink then receives every
// event of the host and each piece of text of each event is searched for those values.
//
// Exception messages are logged by design: the one place that may hold text from inside the application is the
// exception of the single unhandled-exception event, here the secret of the throwing fixture endpoint. It may appear
// there and nowhere else; the secrets of the request may not appear even there.
public class SecretsInLogsTests
{
    // The levels the application runs with by default (Production reads Information) and with the Development default.
    public static TheoryData<string, string?> Levels => new()
    {
        { "Production", null },
        { "Production", "Debug" },
        { "Development", "Debug" },
    };

    private static void AssertNoRequestValues(CollectingSink sink, RequestSecrets secrets, LogEvent? exceptionAllowedIn = null)
    {
        foreach (var logEvent in sink.Events)
        {
            foreach (var text in logEvent.TextsOf())
            {
                foreach (var secret in secrets.Values)
                {
                    Assert.False(
                        text.Contains(secret, StringComparison.OrdinalIgnoreCase),
                        $"A log event holds a request value ('{secret}'): [{logEvent.Level}] {logEvent.RenderMessage(CultureInfo.InvariantCulture)} -> {text}");
                }
            }
        }

        // The secret of the fixture exception may be in the exception of that one event and nowhere else.
        foreach (var logEvent in sink.Events)
        {
            var texts = ReferenceEquals(logEvent, exceptionAllowedIn) ? logEvent.TextsWithoutException() : logEvent.TextsOf();
            foreach (var text in texts)
            {
                Assert.DoesNotContain(ProblemFixtureEndpoint.Secret, text, StringComparison.Ordinal);
            }
        }
    }

    private static void AssertNoSecretsAnywhere(CollectingSink sink, RequestSecrets secrets)
    {
        // The exception of the unhandled-exception event is the one accepted place for text from inside the application.
        var unhandled = Assert.Single(sink.Events, e => e.Exception is not null && e.Exception.ToString().Contains(ProblemFixtureEndpoint.Secret, StringComparison.Ordinal));
        Assert.Equal(LogEventLevel.Error, unhandled.Level);
        AssertNoRequestValues(sink, secrets, unhandled);
    }

    // Breaks when any event, at any level the host writes, holds a header value, the query string or the body of a
    // request, in its message, template, properties (nested too) or exception: a request logging step that records
    // headers or the query string, a handler that logs the rejected value, a log level that the settings raise. The
    // completion events are counted so the check cannot pass on an empty sink.
    [Theory]
    [MemberData(nameof(Levels))]
    public async Task NoLogEventHoldsAHeaderQueryOrBodyValueOfAnyRequest(string environment, string? level)
    {
        using var factory = SecretScenarios.CreateFactory(environment, level);
        var sink = factory.CaptureLogs();
        using var client = factory.CreateClient();

        var run = await SecretScenarios.RunAsync(client);

        Assert.Equal(run.Requests, sink.CompletionEvents().Count);
        Assert.True(sink.Events.Count > run.Requests, "The unhandled exception is logged as well as the completion event.");
        AssertNoSecretsAnywhere(sink, run.Secrets);
    }

    // The same check for each outcome alone, at the most verbose application level, so a failure names the kind of
    // request that leaked.
    [Theory]
    [InlineData("200 version")]
    [InlineData("404 unknown route")]
    [InlineData("400 non-integer query value")]
    [InlineData("400 malformed JSON")]
    [InlineData("400 validation failure")]
    [InlineData("401 anonymous")]
    [InlineData("403 without the permission")]
    [InlineData("200 authenticated with personal claims")]
    [InlineData("403 authenticated with personal claims")]
    [InlineData("429 over the limit")]
    [InlineData("500 unhandled exception")]
    [InlineData("preflight from an unlisted origin")]
    [InlineData("preflight from a listed origin")]
    [InlineData("forwarded headers from an untrusted sender")]
    [InlineData("forwarded headers from the proxy that are not an address")]
    public async Task NoLogEventOfOneKindOfRequestHoldsAHeaderQueryOrBodyValue(string scenarioName)
    {
        var scenario = SecretScenarios.Named(scenarioName);
        using var factory = SecretScenarios.CreateFactory("Development", "Trace");
        var sink = factory.CaptureLogs();
        using var client = factory.CreateClient();
        var secrets = RequestSecrets.Create();

        for (var i = 0; i < scenario.Repeat; i++)
        {
            await SecretScenarios.SendAsync(client, scenario, secrets, i);
        }

        Assert.Equal(scenario.Repeat, sink.CompletionEvents().Count);
        if (scenario.IsUnhandledException)
        {
            AssertNoSecretsAnywhere(sink, secrets);
        }
        else
        {
            AssertNoRequestValues(sink, secrets);
        }
    }

    // Breaks when the framework's own categories, raised as an operator does to investigate (the README shows
    // Microsoft.AspNetCore=Information), write a request value for any request but the forwarded headers ones.
    [Theory]
    [InlineData("Information")]
    [InlineData("Debug")]
    [InlineData("Trace")]
    public async Task RaisingTheFrameworkCategoriesWritesNoHeaderQueryOrBodyValue(string frameworkLevel)
    {
        using var factory = SecretScenarios.CreateFactory("Development", "Debug").WithEarlySetting("Logging:LogLevel:Microsoft.AspNetCore", frameworkLevel);
        var sink = factory.CaptureLogs();
        using var client = factory.CreateClient();

        var run = await SecretScenarios.RunAsync(client, s => !s.IsForwarded);

        Assert.Equal(run.Requests, sink.CompletionEvents().Count);
        AssertNoSecretsAnywhere(sink, run.Secrets);
    }

    // FINDING while this fails: with Microsoft.AspNetCore at Debug or Trace the platform's forwarded headers
    // middleware logs the rejected X-Forwarded-For value ("Unparsable IP: ..."), a header value, which the README
    // says no log event holds. At Information (the level the README shows) it does not.
    [Theory]
    [InlineData("Information")]
    [InlineData("Debug")]
    [InlineData("Trace")]
    public async Task RaisingTheFrameworkCategoriesWritesNoForwardedHeaderValue(string frameworkLevel)
    {
        using var factory = SecretScenarios.CreateFactory("Development", "Debug").WithEarlySetting("Logging:LogLevel:Microsoft.AspNetCore", frameworkLevel);
        var sink = factory.CaptureLogs();
        using var client = factory.CreateClient();

        var run = await SecretScenarios.RunAsync(client, s => s.IsForwarded);

        Assert.Equal(run.Requests, sink.CompletionEvents().Count);
        AssertNoRequestValues(sink, run.Secrets);
    }
}
