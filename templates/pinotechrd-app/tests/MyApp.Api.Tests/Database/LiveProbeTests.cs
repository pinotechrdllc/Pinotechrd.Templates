using System.Diagnostics;
using System.Net;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Web;

namespace MyApp.Api.Tests.Database;

// /health/live never touches the database. Needs Docker for the connection count.
[Collection(PostgresContainer.Name)]
public class LiveProbeTests(PostgresFixture postgres)
{
    private static async Task<(HttpStatusCode Status, TimeSpan Elapsed)> TimedGetAsync(HttpClient client, string path)
    {
        var watch = Stopwatch.StartNew();
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        return (response.StatusCode, watch.Elapsed);
    }

    // Breaks when the liveness probe waits for, or connects to, a database that does not answer: such a probe would
    // take as long as the readiness timeout or more, and the server would see the attempt.
    [Fact]
    public async Task LiveAnswers200QuicklyAndWithoutConnectingWhileTheDatabaseDoesNotAnswer()
    {
        using var server = new SilentServer();
        using var factory = new ApiHostFactory().WithDatabase(server.ConnectionString("Silent-Pw-2"));
        using var client = factory.CreateClient();
        _ = await TimedGetAsync(client, SecurityRequests.Version);

        var live = await TimedGetAsync(client, HealthRegistration.LivePath);

        Assert.Equal(HttpStatusCode.OK, live.Status);
        // A probe that waited for the database would take the whole readiness timeout or more; the connection check below
        // is the exact one, so the time only has to stay clear of a busy machine's delays.
        Assert.True(live.Elapsed < DatabaseComposition.ReadinessTimeout + TimeSpan.FromSeconds(3), $"Live took {live.Elapsed}.");
        Assert.False(server.HasConnectionAttempt, "The liveness probe connected to the database.");
    }

    // Breaks when a liveness probe opens a pooled connection to a reachable database: the server's own view
    // (pg_stat_activity, by a unique application name) shows none after the probes, and shows one after a readiness
    // probe, which proves the count can see this application's connections.
    [Fact]
    public async Task LiveProbesOpenNoConnectionToAReachableDatabaseWhileReadinessDoes()
    {
        var applicationName = "live-probe-" + Guid.NewGuid().ToString("N");
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync()) { ApplicationName = applicationName };
        using var factory = new ApiHostFactory().WithDatabase(connection.ConnectionString);
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            var live = await TimedGetAsync(client, HealthRegistration.LivePath);
            Assert.Equal(HttpStatusCode.OK, live.Status);
        }

        Assert.Equal(0, await postgres.CountConnectionsAsync(applicationName));

        var ready = await TimedGetAsync(client, HealthRegistration.ReadyPath);
        Assert.Equal(HttpStatusCode.OK, ready.Status);
        Assert.True(await postgres.CountConnectionsAsync(applicationName) >= 1);
    }
}
