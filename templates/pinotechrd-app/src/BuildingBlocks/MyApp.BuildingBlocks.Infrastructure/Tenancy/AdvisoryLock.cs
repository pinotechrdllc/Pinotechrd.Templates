using Npgsql;

namespace MyApp.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>
/// A PostgreSQL session advisory lock held on a connection of its own, shared by the tenant migrator and the database
/// migrator. The key is a text; the lock is taken on its 64-bit hash (<c>hashtextextended(key, 0)</c>), so a collision
/// only makes two runs take turns.
/// <para>
/// The lock is taken with <c>pg_try_advisory_lock</c>, retried with a growing pause, not with the blocking
/// <c>pg_advisory_lock</c>: a run that waits gives its connection back to the pool, so runs queued on one lock never use
/// up the connections the run holding it needs. The lock session turns <c>idle_session_timeout</c> off for itself
/// (PostgreSQL 14 or later), so a server limit cannot end it while it waits for the migrations; the pool's reset restores
/// the setting.
/// </para>
/// </summary>
internal static class AdvisoryLock
{
    private const string TryLockSql =
        "SELECT pg_catalog.set_config('idle_session_timeout', '0', false), pg_catalog.pg_try_advisory_lock(pg_catalog.hashtextextended($1, 0))";

    private const string UnlockSql = "SELECT pg_catalog.pg_advisory_unlock(pg_catalog.hashtextextended($1, 0))";

    private static readonly TimeSpan FirstPause = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan LongestPause = TimeSpan.FromSeconds(1);

    /// <summary>Returns an open connection that holds the lock <paramref name="key"/>. While another session holds it, waits without a connection.</summary>
    /// <param name="dataSource">The data source the lock connection comes from.</param>
    /// <param name="key">The lock's key.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>The lock connection; release with <see cref="ReleaseAsync"/>, then dispose it.</returns>
    internal static async Task<NpgsqlConnection> AcquireAsync(NpgsqlDataSource dataSource, string key, CancellationToken cancellationToken)
    {
        var pause = FirstPause;
        while (true)
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var command = new NpgsqlCommand(TryLockSql, connection) { Parameters = { new NpgsqlParameter { Value = key } } };
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.GetBoolean(1))
                {
                    return connection;
                }
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            await connection.DisposeAsync().ConfigureAwait(false);
            await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
            pause = TimeSpan.FromTicks(Math.Min(pause.Ticks * 2, LongestPause.Ticks));
        }
    }

    /// <summary>
    /// Releases the lock. Not cancellable: the lock is released even when the run was cancelled. A broken connection
    /// already lost the lock with its session, so that failure must not hide the run's own outcome.
    /// </summary>
    /// <param name="connection">The lock connection.</param>
    /// <param name="key">The lock's key.</param>
    /// <returns>A task that completes when the lock is released or known to be gone.</returns>
    internal static async Task ReleaseAsync(NpgsqlConnection connection, string key)
    {
        try
        {
            await using var command = new NpgsqlCommand(UnlockSql, connection) { Parameters = { new NpgsqlParameter { Value = key } } };
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // The session, and its lock, ended with the connection.
        }
        catch (InvalidOperationException)
        {
            // The connection is no longer open: same as above.
        }
    }
}
