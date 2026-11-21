using MyApp.BuildingBlocks.Infrastructure.Database;
using Npgsql;

namespace MyApp.Api.Tests.Database;

// The shared session advisory lock of the migrators and the bootstrap command, against a real server. Each test has its own
// database and a data source of one connection, so a waiter that kept its connection checked out would be seen at once.
[Collection(PostgresContainer.Name)]
public class AdvisoryLockTests(PostgresFixture postgres)
{
    private const string LockedBy =
        "SELECT count(*) FROM pg_catalog.pg_locks l, (SELECT pg_catalog.hashtextextended($1, 0) AS k) h "
        + "WHERE l.locktype = 'advisory' AND l.granted AND l.objsubid = 1 AND l.pid = $2 "
        + "AND l.classid::bigint = ((h.k >> 32) & 4294967295) AND l.objid::bigint = (h.k & 4294967295) "
        + "AND l.database = (SELECT oid FROM pg_catalog.pg_database WHERE datname = pg_catalog.current_database())";

    private static readonly TimeSpan StillWaiting = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(15);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string NewKey() => "advisory-test-" + Guid.NewGuid().ToString("N");

    // A data source of one pooled connection, with a server-side idle_session_timeout the lock session must turn off.
    private static NpgsqlDataSource OneConnectionDataSource(string database) =>
        NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(database)
        {
            MaxPoolSize = 1,
            Timeout = 5,
            ApplicationName = "advisory-" + Guid.NewGuid().ToString("N")[..12],
            Options = "-c idle_session_timeout=600000",
        }.ConnectionString);

    private static async Task<NpgsqlConnection> OpenUnpooledAsync(string database)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(database) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(Token);
        return connection;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, params object[] values)
    {
#pragma warning disable CA2100 // The SQL is a constant of the tests.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        return (T)(await command.ExecuteScalarAsync(Token))!;
    }

    private static async Task LockAsync(NpgsqlConnection connection, string key)
    {
        await using var command = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_lock(pg_catalog.hashtextextended($1, 0))", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = key });
        await command.ExecuteNonQueryAsync(Token);
    }

    private static Task<bool> TryLockAsync(NpgsqlConnection connection, string key) =>
        ScalarAsync<bool>(connection, "SELECT pg_catalog.pg_try_advisory_lock(pg_catalog.hashtextextended($1, 0))", key);

    private static Task<bool> UnlockAsync(NpgsqlConnection connection, string key) =>
        ScalarAsync<bool>(connection, "SELECT pg_catalog.pg_advisory_unlock(pg_catalog.hashtextextended($1, 0))", key);

    // Whether a fresh session can take the lock now (it gives it back at once).
    private static async Task<bool> IsFreeAsync(string database, string key)
    {
        await using var probe = await OpenUnpooledAsync(database);
        var taken = await TryLockAsync(probe, key);
        if (taken)
        {
            await UnlockAsync(probe, key);
        }

        return taken;
    }

    // Proves the data source's only connection is in the pool: opening one does not wait for the pool.
    private static async Task AssertPoolConnectionIsFreeAsync(NpgsqlDataSource dataSource)
    {
        using var within = CancellationTokenSource.CreateLinkedTokenSource(Token);
        within.CancelAfter(TimeSpan.FromSeconds(4));
        await using var connection = await dataSource.OpenConnectionAsync(within.Token);
        Assert.Equal(1, await ScalarAsync<int>(connection, "SELECT 1"));
    }

    // Breaks when the returned connection is closed, its session does not hold the advisory lock on hashtextextended(key, 0)
    // (granted, objsubid 1, high and low 32 bits in classid and objid), or it keeps the server's idle_session_timeout.
    [Fact]
    public async Task AcquireReturnsAnOpenConnectionWhoseSessionHoldsTheLockWithIdleSessionTimeoutOff()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var dataSource = OneConnectionDataSource(database);
        var key = NewKey();

        await using var connection = await AdvisoryLock.AcquireAsync(dataSource, key, Token);
        try
        {
            Assert.Equal(System.Data.ConnectionState.Open, connection.State);
            await using var observer = await OpenUnpooledAsync(database);
            Assert.Equal(1L, await ScalarAsync<long>(observer, LockedBy, key, connection.ProcessID));
            Assert.Equal("0", await ScalarAsync<string>(connection, "SHOW idle_session_timeout"));
            Assert.False(await IsFreeAsync(database, key));
        }
        finally
        {
            await AdvisoryLock.ReleaseAsync(connection, key);
        }
    }

    // Breaks when Acquire returns while another session holds the key, keeps the pool's connection checked out between
    // tries (a pool of one could not serve anybody else), or never returns after the other session unlocks.
    [Fact]
    public async Task AcquireWaitsWithoutHoldingAConnectionAndReturnsOnceTheOtherSessionUnlocks()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var dataSource = OneConnectionDataSource(database);
        var key = NewKey();
        await using var holder = await OpenUnpooledAsync(database);
        await LockAsync(holder, key);

        Task<NpgsqlConnection> waiting;
        try
        {
            waiting = AdvisoryLock.AcquireAsync(dataSource, key, Token);
            await Task.WhenAny(waiting, Task.Delay(StillWaiting, Token));
            Assert.False(waiting.IsCompleted, "Acquire returned while another session held the lock.");

            for (var i = 0; i < 3; i++)
            {
                await AssertPoolConnectionIsFreeAsync(dataSource);
                await Task.Delay(300, Token);
            }

            Assert.False(waiting.IsCompleted, "Acquire returned while another session held the lock.");
        }
        finally
        {
            await UnlockAsync(holder, key);
        }

        await using var connection = await waiting.WaitAsync(Generous, Token);
        try
        {
            Assert.Equal(1L, await ScalarAsync<long>(holder, LockedBy, key, connection.ProcessID));
        }
        finally
        {
            await AdvisoryLock.ReleaseAsync(connection, key);
        }
    }

    // Breaks when cancelling the wait does not throw OperationCanceledException, or leaves the lock or the pool's
    // connection held by the cancelled waiter.
    [Fact]
    public async Task CancellingTheWaitThrowsOperationCanceledAndLeavesNoLockAndNoConnectionHeld()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var dataSource = OneConnectionDataSource(database);
        var key = NewKey();
        await using var holder = await OpenUnpooledAsync(database);
        await LockAsync(holder, key);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var waiting = AdvisoryLock.AcquireAsync(dataSource, key, cancel.Token);
        try
        {
            await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(700), Token));
            Assert.False(waiting.IsCompleted, "Acquire returned while another session held the lock.");
            await cancel.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Generous, Token));
        }
        finally
        {
            await UnlockAsync(holder, key);
        }

        await AssertPoolConnectionIsFreeAsync(dataSource);
        Assert.Equal(0L, await ScalarAsync<long>(
            holder,
            "SELECT count(*) FROM pg_catalog.pg_locks WHERE locktype = 'advisory' AND pid <> pg_catalog.pg_backend_pid() "
            + "AND database = (SELECT oid FROM pg_catalog.pg_database WHERE datname = pg_catalog.current_database())"));
        Assert.True(await IsFreeAsync(database, key), "The cancelled waiter left the lock taken.");
    }

    // Breaks when Release does not give the lock back (another session's pg_try_advisory_lock still fails).
    [Fact]
    public async Task ReleaseFreesTheLockForAnotherSession()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var dataSource = OneConnectionDataSource(database);
        var key = NewKey();
        await using var connection = await AdvisoryLock.AcquireAsync(dataSource, key, Token);
        Assert.False(await IsFreeAsync(database, key));

        await AdvisoryLock.ReleaseAsync(connection, key);

        Assert.True(await IsFreeAsync(database, key));
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    // Breaks when Release throws on a connection that is already closed or disposed (it would hide the run's own outcome).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleaseOfAClosedOrDisposedConnectionDoesNotThrow(bool dispose)
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var dataSource = OneConnectionDataSource(database);
        var key = NewKey();
        var connection = await AdvisoryLock.AcquireAsync(dataSource, key, Token);
        if (dispose)
        {
            await connection.DisposeAsync();
        }
        else
        {
            await connection.CloseAsync();
        }

        var failure = await Record.ExceptionAsync(() => AdvisoryLock.ReleaseAsync(connection, key));

        Assert.Null(failure);
        await connection.DisposeAsync();
    }

    // Breaks when Release throws on a connection whose backend was terminated, or the terminated session's lock survives.
    [Fact]
    public async Task ReleaseOfAConnectionWhoseBackendWasTerminatedDoesNotThrowAndTheLockIsGone()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var dataSource = OneConnectionDataSource(database);
        var key = NewKey();
        await using var connection = await AdvisoryLock.AcquireAsync(dataSource, key, Token);
        var pid = connection.ProcessID;
        await using (var killer = await OpenUnpooledAsync(database))
        {
            Assert.True(await ScalarAsync<bool>(killer, "SELECT pg_catalog.pg_terminate_backend($1, 10000)", pid));
        }

        var failure = await Record.ExceptionAsync(() => AdvisoryLock.ReleaseAsync(connection, key));

        Assert.Null(failure);
        Assert.True(await IsFreeAsync(database, key));
    }

    // Breaks when two different keys block each other (the key is ignored or both hash to one lock).
    [Fact]
    public async Task TwoDifferentKeysDoNotBlockEachOther()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var first = NpgsqlDataSource.Create(database);
        await using var second = NpgsqlDataSource.Create(database);
        var (keyA, keyB) = (NewKey(), NewKey());

        await using var a = await AdvisoryLock.AcquireAsync(first, keyA, Token);
        try
        {
            await using var b = await AdvisoryLock.AcquireAsync(second, keyB, Token).WaitAsync(Generous, Token);
            try
            {
                Assert.NotEqual(a.ProcessID, b.ProcessID);
                await using var observer = await OpenUnpooledAsync(database);
                Assert.Equal(1L, await ScalarAsync<long>(observer, LockedBy, keyA, a.ProcessID));
                Assert.Equal(1L, await ScalarAsync<long>(observer, LockedBy, keyB, b.ProcessID));
            }
            finally
            {
                await AdvisoryLock.ReleaseAsync(b, keyB);
            }
        }
        finally
        {
            await AdvisoryLock.ReleaseAsync(a, keyA);
        }
    }
}
