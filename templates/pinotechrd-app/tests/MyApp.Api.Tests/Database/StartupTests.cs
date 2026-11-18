using System.Diagnostics;
using System.Net;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Web;
using MyApp.Infrastructure.Database;

namespace MyApp.Api.Tests.Database;

// Criteria 4 and 5: the connection string is checked when the host starts, without a connection, and the check never
// repeats the value. None of these tests needs a database or Docker.
public class StartupTests
{
    private const string Password = "Pw-Secret-9f2c";
    private const string User = "svc_user_x";
    private const string Host = "db.internal.example";

    // Breaks when a missing, empty or blank connection string no longer stops the host with a validation error that
    // names ConnectionStrings:Database.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t \r\n")]
    public void UnsetConnectionStringStopsTheHostAndTheErrorNamesTheSetting(string? value)
    {
        using var factory = new ApiHostFactory();
        _ = value is null ? factory.WithoutDatabaseConnectionString() : factory.WithDatabase(value);

        StartupFailure.AssertOptionsInvalid(factory, "ConnectionStrings:Database");

        Assert.Equal("ConnectionStrings:Database", DatabaseRegistration.ConnectionStringKey);
    }

    // Breaks when an invalid value is accepted at start, when the error does not name the setting, or when the error
    // (message, inner exceptions, stack traces) or any log event quotes the value, its password, user or host.
    [Theory]
    [InlineData("this is not a connection string at all " + Password)]
    [InlineData("Database=app;Username=" + User + ";Password=" + Password)]
    [InlineData("Host=" + Host + ";Username=" + User + ";Password=" + Password + ";Not A Keyword=1")]
    [InlineData("Host=" + Host + ";Port=not-a-number;Username=" + User + ";Password=" + Password)]
    public void InvalidConnectionStringStopsTheHostAndNothingQuotesTheValue(string value)
    {
        using var factory = new ApiHostFactory().WithDatabase(value);
        var logs = factory.CaptureLogs();

        StartupFailure.AssertOptionsInvalidWithout(factory, DatabaseRegistration.ConnectionStringKey, value, Password, User, Host);

        foreach (var text in logs.Events.SelectMany(e => e.TextsOf()))
        {
            Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
            Assert.DoesNotContain(value, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Host, text, StringComparison.Ordinal);
        }
    }

    // Breaks when the error for a missing value quotes a connection string from somewhere else, or the logs do.
    [Fact]
    public void MissingConnectionStringErrorAndLogsContainNoConnectionStringText()
    {
        using var factory = new ApiHostFactory().WithoutDatabaseConnectionString();
        var logs = factory.CaptureLogs();

        StartupFailure.AssertOptionsInvalidWithout(
            factory, DatabaseRegistration.ConnectionStringKey, TestDatabase.PlaceholderPassword, "placeholder_user");

        Assert.DoesNotContain(logs.Events.SelectMany(e => e.TextsOf()), t => t.Contains(TestDatabase.PlaceholderPassword, StringComparison.Ordinal));
    }

    // Breaks when starting the host (or serving the version and the liveness probe) opens a database connection: the
    // connection string points to a server that accepts connections and never answers, so a connection attempt would
    // be visible at the socket, and the readiness probe at the end proves the detector sees one.
    [Fact]
    public async Task StartingTheHostAndServingVersionAndLiveOpensNoDatabaseConnection()
    {
        using var server = new SilentServer();
        using var factory = new ApiHostFactory().WithDatabase(server.ConnectionString("Silent-Pw-1"));
        var watch = Stopwatch.StartNew();
        using var client = factory.CreateClient();

        using var version = await client.GetAsync(SecurityRequests.Version, TestContext.Current.CancellationToken);
        using var live = await client.GetAsync(HealthRegistration.LivePath, TestContext.Current.CancellationToken);
        watch.Stop();

        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Start and two requests took {watch.Elapsed}.");
        Assert.False(server.HasConnectionAttempt, "The host connected to the database before anything needed it.");

        using var ready = await client.GetAsync(HealthRegistration.ReadyPath, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.True(server.HasConnectionAttempt, "The detector saw no connection attempt from the readiness probe.");
    }

    // Breaks when a valid connection string for a server that is down stops the start (the check must not connect).
    [Fact]
    public async Task ValidConnectionStringForAServerThatIsDownStartsTheHost()
    {
        using var factory = new ApiHostFactory().WithDatabase(TestDatabase.PlaceholderConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(SecurityRequests.Version, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
