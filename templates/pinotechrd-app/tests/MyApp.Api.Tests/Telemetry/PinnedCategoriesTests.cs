using System.Net;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Web.Telemetry;
using Serilog.Events;

namespace MyApp.Api.Tests.Telemetry;

// The framework categories that write a header value, the query string or the client address stay at
// Warning when a parent category or the default is raised, and change only when named exactly. Every expectation is
// taken from the public list, not from a copy of it.
public class PinnedCategoriesTests
{
    public static TheoryData<string> Categories => [.. TelemetryRegistration.PinnedLogCategories];

    public static TheoryData<string, string> ParentsAndLevels
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var parent in new[] { "Default", "Microsoft.AspNetCore", "Microsoft" })
            {
                foreach (var level in new[] { "Debug", "Trace" })
                {
                    data.Add(parent, level);
                }
            }

            return data;
        }
    }

    private static string Key(string category) => "Logging:LogLevel:" + category;

    private static bool IsPinned(LogEvent logEvent) => TelemetryRegistration.PinnedLogCategories.Any(logEvent.IsFrom);

    // Breaks when the list is emptied.
    [Fact]
    public void PinnedCategoryListIsNotEmpty() => Assert.NotEmpty(TelemetryRegistration.PinnedLogCategories);

    // Breaks when raising a parent category or the default also lowers a pinned category: it would then write below
    // Warning, with the values the README lists for it. The parent really is raised: events of unpinned categories
    // below Warning are present, so the check cannot pass because nothing was raised.
    [Theory]
    [MemberData(nameof(ParentsAndLevels))]
    public async Task RaisingAParentOrTheDefaultDoesNotMakeAPinnedCategoryWriteBelowWarning(string parent, string level)
    {
        using var factory = SecretScenarios.CreateFactory("Development", null).WithEarlySetting(Key(parent), level);
        if (parent != "Default")
        {
            factory.WithEarlySetting(Key("Default"), level);
        }

        var sink = factory.CaptureLogs();
        using var client = factory.CreateClient();

        var run = await SecretScenarios.RunAsync(client);

        Assert.Equal(run.Requests, sink.CompletionEvents().Count);
        Assert.Contains(sink.Events, e => e.Level < LogEventLevel.Warning && !IsPinned(e) && !LogCapture.IsCompletion(e));
        foreach (var category in TelemetryRegistration.PinnedLogCategories)
        {
            Assert.DoesNotContain(sink.Events, e => e.IsFrom(category) && e.Level < LogEventLevel.Warning);
        }
    }

    // Breaks when naming a pinned category exactly does not lower it: the operator's deliberate choice must work. The
    // Kestrel bad-requests category cannot write under the in-memory server, so for it only a clean start is shown.
    [Theory]
    [MemberData(nameof(Categories))]
    public async Task NamingAPinnedCategoryExactlyLowersIt(string category)
    {
        using var factory = SecretScenarios.CreateFactory("Development", null).WithEarlySetting(Key(category), "Trace");
        var sink = factory.CaptureLogs();
        using var client = factory.CreateClient();

        var run = await SecretScenarios.RunAsync(client);

        Assert.Equal(run.Requests, sink.CompletionEvents().Count);
        var producible = category.EndsWith("Hosting.Diagnostics", StringComparison.Ordinal)
            || category.EndsWith("HttpOverrides", StringComparison.Ordinal)
            || category.EndsWith("Cors", StringComparison.Ordinal);
        if (producible)
        {
            Assert.Contains(sink.Events, e => e.IsFrom(category) && e.Level < LogEventLevel.Warning);
        }
    }

    // Breaks when a garbage X-Forwarded-For value from the configured proxy, or an Origin value, reaches an event while
    // only the parents are raised. The second half of each row breaks when naming the category does not write the
    // value: the test could then not see the leak it guards against.
    [Theory]
    [InlineData("Microsoft.AspNetCore.HttpOverrides", "forwarded")]
    [InlineData("Microsoft.AspNetCore.Cors", "origin")]
    public async Task ForwardedForAndOriginValuesAreLoggedOnlyWhenTheirCategoryIsNamed(string category, string kind)
    {
        Assert.Contains(category, TelemetryRegistration.PinnedLogCategories);
        var id = Guid.NewGuid().ToString("N");
        var value = kind == "forwarded" ? $"s3cr3t-xff-{id}" : $"https://s3cr3t-origin-{id}.example.com";

        async Task<List<string>> TextsAsync(params (string Key, string Level)[] settings)
        {
            using var factory = SecretScenarios.CreateFactory("Production", null);
            foreach (var (key, level) in settings)
            {
                factory.WithEarlySetting(key, level);
            }

            var sink = factory.CaptureLogs();
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, SecurityRequests.Version);
            request.Headers.TryAddWithoutValidation(ApiHostFactory.RemoteAddressHeader, kind == "forwarded" ? SecretScenarios.Proxy : "10.30.0.1");
            request.Headers.TryAddWithoutValidation(kind == "forwarded" ? "X-Forwarded-For" : "Origin", value);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return [.. sink.Events.SelectMany(e => e.TextsOf())];
        }

        var raised = await TextsAsync((Key("Default"), "Trace"), (Key("Microsoft.AspNetCore"), "Trace"), (Key("Microsoft"), "Trace"));
        var named = await TextsAsync((Key("Default"), "Warning"), (Key(category), "Debug"));

        Assert.DoesNotContain(raised, t => t.Contains(value, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(named, t => t.Contains(value, StringComparison.OrdinalIgnoreCase));
    }
}
