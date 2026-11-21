using MyApp.Api.Tests.Database;
using MyApp.Migrator.Tests.Support;
using Npgsql;

namespace MyApp.Migrator.Tests.Bootstrap;

// The session advisory lock that makes bootstrap runs take turns, observed from outside: a session that holds the key
// holds the run back, and no run leaves a lock behind. Real PostgreSQL server, black-box through the command.
[Collection(PostgresContainer.Name)]
public class BootstrapLockTests(PostgresFixture postgres)
{
    // The key of MigratorBootstrap.LockKey (internal to the migrator).
    private const string BootstrapLockKey = "operator-bootstrap";

    private const string AdvisoryLocksOfThisDatabase =
        "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' "
        + "AND database = (SELECT oid FROM pg_database WHERE datname = current_database())";

    private static async Task SetLockAsync(NpgsqlConnection connection, string function)
    {
#pragma warning disable CA2100 // The function name is one of two constants of this class.
        await using var command = new NpgsqlCommand($"SELECT pg_catalog.{function}(pg_catalog.hashtextextended($1, 0))", connection);
#pragma warning restore CA2100
        command.Parameters.AddWithValue(BootstrapLockKey);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    // Breaks when bootstrap does not take the operator-bootstrap lock (it finishes while another session holds it), never
    // finishes or fails once the lock is given back, or leaves an advisory lock in the database after it exits.
    [Fact]
    public async Task ABootstrapWaitsWhileAnotherSessionHoldsTheLockThenExitsZeroAndLeavesNoAdvisoryLock()
    {
        var database = await BootstrapSupport.MigratedDatabaseAsync(postgres);
        Task<BootstrapRun> running;
        await using (var holder = await Db.OpenAsync(database))
        {
            await SetLockAsync(holder, "pg_advisory_lock");
            try
            {
                running = BootstrapRun.ExecuteAsync(database, BootstrapRun.GoodPassword);
                await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
                Assert.False(running.IsCompleted, "Bootstrap finished while another session held its lock.");
                Assert.Empty(await BootstrapSupport.OperatorTenantsAsync(database));
            }
            finally
            {
                await SetLockAsync(holder, "pg_advisory_unlock");
            }
        }

        var run = await running.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

        Assert.True(run.ExitCode == 0, run.Report);
        Assert.Single(await BootstrapSupport.OperatorTenantsAsync(database));
        Assert.Equal(0L, await Db.CountAsync(database, AdvisoryLocksOfThisDatabase));
    }

    // Breaks when a bootstrap run that ends in a refusal (no password, no administrator yet) keeps its advisory lock, so
    // the next run would wait forever.
    [Fact]
    public async Task ARefusedBootstrapLeavesNoAdvisoryLockAndTheNextRunExitsZero()
    {
        var database = await BootstrapSupport.MigratedDatabaseAsync(postgres);

        var refused = await BootstrapRun.ExecuteAsync(database, password: null);

        Assert.True(refused.ExitCode != 0, refused.Report);
        Assert.Equal(0L, await Db.CountAsync(database, AdvisoryLocksOfThisDatabase));
        var again = await BootstrapRun.ExecuteAsync(database, BootstrapRun.GoodPassword)
            .WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.True(again.ExitCode == 0, again.Report);
        Assert.Equal(0L, await Db.CountAsync(database, AdvisoryLocksOfThisDatabase));
    }
}
