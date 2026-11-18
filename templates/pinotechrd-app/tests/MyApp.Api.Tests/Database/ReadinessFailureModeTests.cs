using System.Diagnostics;
using System.Net;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Web;
using Npgsql;

namespace MyApp.Api.Tests.Database;

// The readiness bound holds when the server stalls after a connection is pooled, one connection
// attempt at a time is in flight against a server that does not answer, and readiness recovers. The stall is made with
// a proxy between the host and the shared container that stops forwarding (the container is not touched). Needs Docker.
[Collection(PostgresContainer.Name)]
public class ReadinessFailureModeTests(PostgresFixture postgres)
{
    private static readonly TimeSpan Bound = DatabaseComposition.ReadinessTimeout + TimeSpan.FromSeconds(5);

    // The README: an attempt that has not finished ends after at most the connect Timeout (15 seconds), and the next
    // probe after that reports 200. Generous on top of it.
    private static readonly TimeSpan RecoveryBound = TimeSpan.FromSeconds(45);

    private async Task<string> ThroughAsync(FreezableProxy proxy)
    {
        var connection = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync()) { Host = "127.0.0.1", Port = proxy.Port };
        return connection.ConnectionString;
    }

    private static async Task<(HttpStatusCode Status, string Body, TimeSpan Elapsed)> GetAsync(HttpClient client, string path)
    {
        var watch = Stopwatch.StartNew();
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, body, watch.Elapsed);
    }

    private static async Task AssertRecoversAsync(HttpClient client)
    {
        var watch = Stopwatch.StartNew();
        HttpStatusCode last;
        do
        {
            last = (await GetAsync(client, HealthRegistration.ReadyPath)).Status;
            if (last == HttpStatusCode.OK)
            {
                return;
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
        while (watch.Elapsed < RecoveryBound);

        Assert.Fail($"Readiness did not return to 200 within {RecoveryBound}; last answer {last}.");
    }

    // Breaks when a server that stops answering in the middle of a query, on a connection that is already pooled,
    // keeps readiness waiting past its limit, takes liveness down, leaks a secret, or leaves readiness failing after
    // the server answers again.
    [Fact]
    public async Task ServerThatStallsAfterAConnectionIsPooledMakesReadyAnswer503WithinTheBoundAndReadinessRecovers()
    {
        using var proxy = new FreezableProxy(postgres.Host, postgres.Port);
        var connectionString = await ThroughAsync(proxy);
        using var factory = new ApiHostFactory().WithDatabase(connectionString);
        var logs = factory.CaptureLogs();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, HealthRegistration.ReadyPath)).Status);

        proxy.Freeze();
        var ready = await GetAsync(client, HealthRegistration.ReadyPath);
        var live = await GetAsync(client, HealthRegistration.LivePath);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.Status);
        Assert.True(ready.Elapsed < Bound, $"Readiness answered after {ready.Elapsed}, bound {Bound}.");
        Assert.Equal("Unhealthy", ready.Body);
        Assert.Equal(HttpStatusCode.OK, live.Status);
        ReadinessTests.AssertBodyHasNo(ready.Body, connectionString, PostgresFixture.Password, PostgresFixture.Username);
        ReadinessTests.AssertLogsHaveNo(logs, connectionString, PostgresFixture.Password);

        proxy.Thaw();
        await AssertRecoversAsync(client);
    }

    // Breaks when concurrent or successive probes against a server that accepts and never answers each start their own
    // connection attempt (the server would count more than one), or when they take longer than the bound.
    [Fact]
    public async Task ConcurrentAndRepeatedProbesAgainstASilentServerOpenExactlyOneConnection()
    {
        using var server = new CountingSilentServer();
        // A long connect timeout, so the first attempt is certainly still pending when the last probe is made.
        using var factory = new ApiHostFactory().WithDatabase(server.ConnectionString("Silent-Pw-3") + ";Timeout=120");
        using var client = factory.CreateClient();

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => GetAsync(client, HealthRegistration.ReadyPath)));
        var sequential = new List<(HttpStatusCode Status, string Body, TimeSpan Elapsed)>();
        for (var i = 0; i < 2; i++)
        {
            sequential.Add(await GetAsync(client, HealthRegistration.ReadyPath));
        }

        foreach (var answer in concurrent.Concat(sequential))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, answer.Status);
            Assert.True(answer.Elapsed < Bound, $"Readiness answered after {answer.Elapsed}, bound {Bound}.");
        }

        Assert.True(await TelemetryCapture.UntilAsync(() => server.Accepted >= 1), "The probe never reached the server.");
        Assert.Equal(1, server.Accepted);
    }

    // Breaks when probes against a server that does not forward start more than one connection attempt, or when
    // readiness stays failing after the server answers again (the held attempt completes or a new one succeeds).
    [Fact]
    public async Task ProbesAgainstAFrozenServerHoldOneAttemptAndReadinessRecoversWhenItAnswers()
    {
        using var proxy = new FreezableProxy(postgres.Host, postgres.Port, frozen: true);
        using var factory = new ApiHostFactory().WithDatabase(await ThroughAsync(proxy));
        using var client = factory.CreateClient();

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => GetAsync(client, HealthRegistration.ReadyPath)));
        var again = await GetAsync(client, HealthRegistration.ReadyPath);

        Assert.All(concurrent.Append(again), a =>
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, a.Status);
            Assert.True(a.Elapsed < Bound, $"Readiness answered after {a.Elapsed}, bound {Bound}.");
        });
        Assert.Equal(1, proxy.Accepted);

        proxy.Thaw();
        await AssertRecoversAsync(client);
    }
}
