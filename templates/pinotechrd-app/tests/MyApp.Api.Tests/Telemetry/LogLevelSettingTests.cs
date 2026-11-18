using System.Net;
using MyApp.Api.Tests.Support;
using Serilog.Events;

namespace MyApp.Api.Tests.Telemetry;

// The standard Logging:LogLevel section controls what is emitted, and an invalid value stops the host.
public class LogLevelSettingTests
{
    private const string DefaultLevel = "Logging:LogLevel:Default";

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path) =>
        await client.GetAsync(path, TestContext.Current.CancellationToken);

    // Breaks when the default level is ignored and the completion event is written regardless (or never written).
    [Theory]
    [InlineData("Warning", 0)]
    [InlineData("Error", 0)]
    [InlineData("Information", 1)]
    [InlineData("Debug", 1)]
    [InlineData("Trace", 1)]
    public async Task DefaultLevelDecidesWhetherASuccessfulRequestIsLogged(string level, int expectedEvents)
    {
        using var factory = new ApiHostFactory().WithEarlySetting(DefaultLevel, level);
        var sink = factory.CaptureLogs();
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, SecurityRequests.Version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedEvents, sink.CompletionEvents().Count);
    }

    // Breaks when the level is applied to the request log as if it were Information whatever the setting, so a 5xx
    // is hidden at Warning, or the Error event of a failure is lost at Error.
    [Theory]
    [InlineData("Warning")]
    [InlineData("Error")]
    public async Task FailedRequestIsStillLoggedWhenOnlyWarningsAndAboveAreEnabled(string level)
    {
        using var factory = new ApiHostFactory().WithEarlySetting(DefaultLevel, level);
        var sink = factory.CaptureLogs();
        using var client = factory.CreateClient();

        using var notFound = await GetAsync(client, SecurityRequests.Unknown);
        using var failure = await GetAsync(client, SecurityRequests.Throwing);

        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        var completion = Assert.Single(sink.CompletionEvents());
        Assert.Equal(500, completion.ScalarOf(LogCapture.StatusProperty));
        Assert.Equal(LogEventLevel.Error, completion.Level);
    }

    // Breaks when the level None does not switch the events off, or when it is rejected as invalid.
    [Fact]
    public async Task LevelNoneWritesNoEvent()
    {
        using var factory = new ApiHostFactory().WithEarlySetting(DefaultLevel, "None");
        var sink = factory.CaptureLogs();
        using var client = factory.CreateClient();

        using var ok = await GetAsync(client, SecurityRequests.Version);
        using var failure = await GetAsync(client, SecurityRequests.Throwing);

        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        Assert.Empty(sink.Events);
    }

    // Breaks when the level names are case sensitive although the platform's are not.
    [Fact]
    public async Task LevelNamesAreNotCaseSensitive()
    {
        using var factory = new ApiHostFactory().WithEarlySetting(DefaultLevel, "warning");
        var sink = factory.CaptureLogs();
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, SecurityRequests.Version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(sink.CompletionEvents());
    }

    // Breaks when a category named in the section is not honored: the completion event belongs to one category and a
    // level set for it by name must win over the default, in both directions.
    [Theory]
    [InlineData("Information", "Warning", 0)]
    [InlineData("Warning", "Information", 1)]
    public async Task LevelOfTheCategoryOfTheCompletionEventOverridesTheDefault(string defaultLevel, string categoryLevel, int expectedEvents)
    {
        using var factory = new ApiHostFactory()
            .WithEarlySetting(DefaultLevel, defaultLevel)
            .WithEarlySetting("Logging:LogLevel:Serilog.AspNetCore", categoryLevel);
        var sink = factory.CaptureLogs();
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, SecurityRequests.Version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedEvents, sink.CompletionEvents().Count);
    }

    // Breaks when an invalid level is ignored (the host starts with some default) or the error does not name the
    // setting, which would leave an operator guessing which variable is wrong.
    [Theory]
    [InlineData("Loud")]
    [InlineData("")]
    [InlineData("3x")]
    [InlineData("99")]
    public void InvalidDefaultLevelStopsTheHostWithAnErrorThatNamesTheSetting(string level)
    {
        using var factory = new ApiHostFactory().WithEarlySetting(DefaultLevel, level);

        StartupFailure.AssertFailsNaming(factory, DefaultLevel);
    }

    // Breaks when only the Default key is validated: a typo in a category override must stop the host too.
    [Fact]
    public void InvalidCategoryLevelStopsTheHostWithAnErrorThatNamesTheSetting()
    {
        const string Setting = "Logging:LogLevel:Microsoft.AspNetCore";
        using var factory = new ApiHostFactory().WithEarlySetting(Setting, "Loud");

        StartupFailure.AssertFailsNaming(factory, Setting);
    }
}
