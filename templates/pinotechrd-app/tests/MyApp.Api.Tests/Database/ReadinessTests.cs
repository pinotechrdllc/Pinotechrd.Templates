using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Web;
using MyApp.BuildingBlocks.Infrastructure.Database;
using Serilog.Events;

namespace MyApp.Api.Tests.Database;

// Readiness reports the database, liveness does not depend on it, and neither response nor log
// holds a secret. Needs Docker (the shared PostgreSQL container).
[Collection(PostgresContainer.Name)]
public class ReadinessTests(PostgresFixture postgres)
{
    private const string WrongPassword = "Wrong-Pw-Secret-31d8";

    private static readonly TimeSpan Bound = DatabaseComposition.ReadinessTimeout + TimeSpan.FromSeconds(5);

    private record struct Answer(HttpStatusCode Status, string Body, TimeSpan Elapsed);

    private static async Task<Answer> GetAsync(HttpClient client, string path)
    {
        var watch = Stopwatch.StartNew();
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return new Answer(response.StatusCode, body, watch.Elapsed);
    }

    // Breaks when readiness does not run a real query on the shared pool, or liveness breaks with the database up.
    [Fact]
    public async Task ReadyAndLiveAnswer200WhenTheDatabaseIsReachable()
    {
        using var factory = new ApiHostFactory().WithDatabase(await postgres.CreateDatabaseAsync());
        using var client = factory.CreateClient();

        var ready = await GetAsync(client, HealthRegistration.ReadyPath);
        var live = await GetAsync(client, HealthRegistration.LivePath);

        Assert.Equal(HttpStatusCode.OK, ready.Status);
        Assert.Equal("Healthy", ready.Body);
        Assert.Equal(HttpStatusCode.OK, live.Status);
    }

    // Breaks when the readiness check stops being registered with the ready tag, loses its name or its documented
    // timeout (an orchestrator's probe would then time out before the application answers).
    [Fact]
    public void ReadinessCheckIsRegisteredWithTheReadyTagAndTheDocumentedTimeout()
    {
        using var factory = new ApiHostFactory().WithDatabase(TestDatabase.PlaceholderConnectionString);

        var registrations = factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        var database = Assert.Single(registrations, r => r.Name == DatabaseHealthCheck.Name);
        Assert.Contains(HealthRegistration.ReadyTag, database.Tags);
        Assert.Equal(DatabaseComposition.ReadinessTimeout, database.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(2), DatabaseComposition.ReadinessTimeout);
    }

    // Breaks when an unreachable database makes readiness answer anything but 503 within the timeout, when it takes
    // liveness down with it, or when the body or a log event shows a secret.
    [Fact]
    public async Task UnreachableDatabaseMakesReadyAnswer503AndNothingLeaks()
    {
        var connectionString = TestDatabase.PlaceholderConnectionString;

        await AssertUnreadyAndSafeAsync(
            connectionString, TestDatabase.PlaceholderPassword, "placeholder_user", "127.0.0.1", "1", expectedFailureCode: null);
    }

    // Breaks when a server that accepts the connection and never answers makes the probe hang past its timeout.
    [Fact]
    public async Task NonAnsweringDatabaseMakesReadyAnswer503WithinTheTimeoutAndNothingLeaks()
    {
        using var server = new SilentServer();

        await AssertUnreadyAndSafeAsync(
            server.ConnectionString(WrongPassword), WrongPassword, "silent_user", "127.0.0.1", server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), expectedFailureCode: null);

        Assert.True(server.HasConnectionAttempt, "The probe never reached the server.");
    }

    // Breaks when a connection to an address that routes nowhere keeps the probe waiting for the connect timeout of the
    // connection string (15 seconds by default) instead of the readiness timeout.
    [Fact]
    public async Task UnroutableDatabaseAddressMakesReadyAnswer503WithinTheTimeout()
    {
        const string Address = "192.0.2.1";
        var connectionString = $"Host={Address};Port=5432;Database=blackhole_db;Username=blackhole_user;Password={WrongPassword}";

        await AssertUnreadyAndSafeAsync(connectionString, WrongPassword, "blackhole_user", Address, "5432", expectedFailureCode: null);
    }

    // Breaks when a failed login is reported as healthy, takes longer than the bound, leaks the wrong password, the
    // real one or the user, or loses the SQLSTATE the README documents for a failed login.
    [Fact]
    public async Task WrongCredentialsMakeReadyAnswer503AndNothingLeaks()
    {
        var connection = postgres.Connection("postgres");
        connection.Password = WrongPassword;

        await AssertUnreadyAndSafeAsync(
            connection.ConnectionString,
            WrongPassword,
            PostgresFixture.Username,
            postgres.Host,
            postgres.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            expectedFailureCode: "28P01",
            PostgresFixture.Password);
    }

    // Breaks when a database that does not exist is reported as healthy or leaks its name or the credentials.
    [Fact]
    public async Task MissingDatabaseMakesReadyAnswer503AndNothingLeaks()
    {
        var connection = postgres.Connection("missing_" + Guid.NewGuid().ToString("N"));

        await AssertUnreadyAndSafeAsync(
            connection.ConnectionString,
            PostgresFixture.Password,
            PostgresFixture.Username,
            postgres.Host,
            postgres.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            expectedFailureCode: null,
            connection.Database!);
    }

    // Breaks when the healthy response or the healthy run's log events show the connection string or its parts.
    [Fact]
    public async Task HealthyReadyResponseAndLogsContainNoSecret()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        using var factory = new ApiHostFactory().WithDatabase(connectionString);
        var logs = factory.CaptureLogs();
        using var client = factory.CreateClient();

        var ready = await GetAsync(client, HealthRegistration.ReadyPath);

        Assert.Equal(HttpStatusCode.OK, ready.Status);
        AssertBodyHasNo(ready.Body, connectionString, PostgresFixture.Password, PostgresFixture.Username, postgres.Host, postgres.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AssertLogsHaveNo(logs, connectionString, PostgresFixture.Password);
    }

    private static async Task AssertUnreadyAndSafeAsync(
        string connectionString, string password, string user, string host, string port, string? expectedFailureCode, params string[] alsoSecret)
    {
        using var factory = new ApiHostFactory().WithDatabase(connectionString);
        var logs = factory.CaptureLogs();
        using var client = factory.CreateClient();

        var ready = await GetAsync(client, HealthRegistration.ReadyPath);
        var live = await GetAsync(client, HealthRegistration.LivePath);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.Status);
        Assert.True(ready.Elapsed < Bound, $"Readiness answered after {ready.Elapsed}, bound {Bound}.");
        Assert.Equal("Unhealthy", ready.Body);
        Assert.Equal(HttpStatusCode.OK, live.Status);
        AssertBodyHasNo(ready.Body, [connectionString, password, user, host, port, .. alsoSecret]);
        AssertLogsHaveNo(logs, [connectionString, password, .. alsoSecret.Where(s => s == PostgresFixture.Password)]);

        // The failure is logged by the health check service (the README says at Error): without it the checks above
        // would pass on an empty log.
        var failure = logs.Events.Where(e => e.Level == LogEventLevel.Error && e.IsFrom("Microsoft.Extensions.Diagnostics.HealthChecks")).ToList();
        Assert.NotEmpty(failure);
        if (expectedFailureCode is not null)
        {
            Assert.Contains(failure.SelectMany(e => e.TextsOf()), t => t.Contains(expectedFailureCode, StringComparison.Ordinal));
        }
    }

    internal static void AssertBodyHasNo(string body, params string[] secrets)
    {
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
        }

        // Only the status word: no exception type or text.
        Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Npgsql", body, StringComparison.OrdinalIgnoreCase);
        Assert.True(body is "Healthy" or "Unhealthy" or "Degraded", $"Unexpected readiness body '{body}'.");
    }

    internal static void AssertLogsHaveNo(CollectingSink logs, params string[] secrets)
    {
        var texts = logs.Events.SelectMany(e => e.TextsOf()).ToList();
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(texts, t => t.Contains(secret, StringComparison.Ordinal));
        }
    }
}
