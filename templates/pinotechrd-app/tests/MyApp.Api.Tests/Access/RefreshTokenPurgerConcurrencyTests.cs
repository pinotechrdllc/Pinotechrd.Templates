using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.Modules.Access.Infrastructure.Persistence;
using MyApp.Modules.Access.Infrastructure.Security;
using Npgsql;
using Serilog.Events;

namespace MyApp.Api.Tests.Access;

// The purger against other sessions (the tenant's advisory lock, a locked token row, cancellation), the session settings
// it must leave as they were (timeouts, time zone) and the schema it must be bound to. Helpers are in RefreshTokenPurgerTests.cs.
public partial class RefreshTokenPurgerTests
{
    // Families that ended 30 days ago with fixed ids, so ORDER BY id puts them in batches in this order; one token each.
    private static readonly Guid[] Ordered = [.. Enumerable.Range(1, 3).Select(i => Guid.Parse($"00000000-0000-4000-8000-00000000000{i}"))];

    // True when the connection is outside any transaction (now() moves between two statements) and holds no advisory,
    // row or transaction id lock.
    private static async Task<bool> NoTransactionOrLockAsync(NpgsqlConnection connection)
    {
        var first = (DateTime)(await RunAsync(connection, "SELECT now()"))!;
        await Task.Delay(5, Token);
        return (bool)(await RunAsync(connection, """
            SELECT now() <> @first
               AND NOT EXISTS (SELECT 1 FROM pg_locks WHERE pid = pg_backend_pid() AND locktype IN ('advisory', 'transactionid', 'tuple'))
            """, ("first", first)))!;
    }

    private static async Task<object?> RunAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteScalarAsync(Token);
    }

    private static Task<T> OnTenantConnectionAsync<T>(AccessWorld world, Guid tenant, Func<IServiceProvider, NpgsqlConnection, Task<T>> work) =>
        InTenantAsync(world, tenant, async scope =>
            await work(scope, await scope.GetRequiredService<AccessDbContext>().Database.OpenTenantConnectionAsync(Token)));

    private static async Task<NpgsqlConnection> ObserverAsync(AccessWorld world) =>
        await world.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Token);

    private static async Task SeedOrderedAsync(AccessWorld world, Guid tenant) =>
        await ScalarAsync(world, tenant, """
            WITH f AS (
                INSERT INTO refresh_token_families (id, user_id, created_at, absolute_expires_at)
                SELECT id, gen_random_uuid(), now() - interval '60 days', now() - interval '30 days' FROM unnest(@ids) AS id
                RETURNING id, user_id)
            INSERT INTO refresh_tokens (id, family_id, token_hash, user_id, created_at, expires_at)
            SELECT gen_random_uuid(), f.id, uuid_send(gen_random_uuid()), f.user_id, now() - interval '60 days', now() - interval '30 days' FROM f
            """, ("ids", Ordered));

    // Another transaction of the tenant holding FOR UPDATE on every token of a family until disposed (then it commits).
    private sealed class TokenRowLock : IAsyncDisposable
    {
        private readonly AsyncServiceScope _scope;
        private readonly NpgsqlTransaction _transaction;

        private TokenRowLock(AsyncServiceScope scope, NpgsqlTransaction transaction) => (_scope, _transaction) = (scope, transaction);

        public static async Task<TokenRowLock> TakeAsync(AccessWorld world, Guid tenant, Guid family)
        {
            var scope = world.Services.CreateAsyncScope();
            AccessDatabase.EnterTenant(scope.ServiceProvider, tenant);
            var connection = await scope.ServiceProvider.GetRequiredService<AccessDbContext>().Database.OpenTenantConnectionAsync(Token);
            var transaction = await connection.BeginTransactionAsync(Token);
            await using var command = new NpgsqlCommand("SELECT id FROM refresh_tokens WHERE family_id = @f FOR UPDATE", connection, transaction);
            command.Parameters.AddWithValue("f", family);
            Assert.Equal(-1, await command.ExecuteNonQueryAsync(Token));
            return new TokenRowLock(scope, transaction);
        }

        public async ValueTask DisposeAsync()
        {
            await _transaction.CommitAsync(Token);
            await _transaction.DisposeAsync();
            await _scope.DisposeAsync();
        }
    }

    // ---- Other sessions ----

    // Breaks when the purge waits for, or ignores, the tenant's advisory lock held by another session (FOR UPDATE SKIP
    // LOCKED alone would delete the due rows here), or when the lock key is not 'refresh-token-purge:' || schema.
    [Fact]
    public async Task WhileAnotherSessionHoldsTheTenantsPurgeLockThePurgeReturnsZeroAtOnceAndDeletesNothing()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await SeedEndedAsync(world, world.TenantA, 3);
        await SeedEndedAsync(world, world.TenantB, 1);
        await using var holder = await ObserverAsync(world);
        const string Key = "hashtextextended('refresh-token-purge:' || @s, 0)";
        await RunAsync(holder, $"SELECT pg_advisory_lock({Key})", ("s", TenantSchemaName.For(world.TenantA)));

        var clock = Stopwatch.StartNew();
        var deleted = await PurgeAsync(world, world.TenantA);
        clock.Stop();

        Assert.Equal(0, deleted);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"The purge took {clock.Elapsed}.");
        Assert.Equal(3L, await AllFamiliesAsync(world, world.TenantA));
        Assert.Equal(1, await PurgeAsync(world, world.TenantB)); // B's key is another: B is purged meanwhile.

        Assert.True((bool)(await RunAsync(holder, $"SELECT pg_advisory_unlock({Key})", ("s", TenantSchemaName.For(world.TenantA))))!);
        Assert.Equal(3, await PurgeAsync(world, world.TenantA));
        Assert.Equal(0L, await AllFamiliesAsync(world, world.TenantA));
    }

    // Breaks when a locked token row makes the purge throw, wait past LockTimeout, lose the batches committed before it,
    // skip the warning, or leave a transaction or lock behind; or when the family is not deleted once the lock is gone.
    [Fact]
    public async Task ALockedTokenRowEndsThePurgeWithWhatWasCommittedLogsAWarningAndLeavesNothingLocked()
    {
        CollectingSink logs = null!;
        using var world = await AccessWorld.StartAsync(postgres, f =>
        {
            logs = f.CaptureLogs();
            return f.WithSetting(Purge + "BatchSize", "2").WithSetting(Purge + "LockTimeout", "00:00:00.2");
        });
        await SeedOrderedAsync(world, world.TenantA);

        int deleted;
        bool clean;
        await using (await TokenRowLock.TakeAsync(world, world.TenantA, Ordered[2]))
        {
            (deleted, clean) = await OnTenantConnectionAsync(world, world.TenantA, async (scope, connection) =>
            {
                var count = await scope.GetRequiredService<RefreshTokenPurger>().PurgeCurrentTenantAsync(Token);
                return (count, await NoTransactionOrLockAsync(connection));
            });
            Assert.Equal(1L, await FamiliesAsync(world, world.TenantA, Ordered[2]));
        }

        Assert.Equal(2, deleted);
        Assert.True(clean);
        Assert.Equal(1L, await AllFamiliesAsync(world, world.TenantA));
        var warning = Assert.Single(logs.Events, e => e.IsFrom(typeof(RefreshTokenPurger).FullName!) && e.Level == LogEventLevel.Warning);
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, warning.ScalarOf("SqlState"));
        Assert.Equal(2, warning.ScalarOf("Count"));

        Assert.Equal(1, await PurgeAsync(world, world.TenantA));
        Assert.Equal(0L, await AllFamiliesAsync(world, world.TenantA));
    }

    // Breaks when a cancellation while blocked is swallowed as a timeout, the batch commits, or the session is left
    // idle in a transaction or holding the tenant's advisory lock.
    [Fact]
    public async Task CancellingAPurgeBlockedOnATokenRowThrowsRollsBackAndLeavesNoLockOrTransaction()
    {
        using var world = await AccessWorld.StartAsync(postgres, f => f
            .WithSetting(Purge + "StatementTimeout", "00:01:00").WithSetting(Purge + "LockTimeout", "00:01:00"));
        await SeedOrderedAsync(world, world.TenantA);
        await using var observer = await ObserverAsync(world);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);

        await using (await TokenRowLock.TakeAsync(world, world.TenantA, Ordered[0]))
        {
            var (error, pid, clean) = await OnTenantConnectionAsync(world, world.TenantA, async (scope, connection) =>
            {
                var pid = (int)(await RunAsync(connection, "SELECT pg_backend_pid()"))!;
                var purge = scope.GetRequiredService<RefreshTokenPurger>().PurgeCurrentTenantAsync(cancel.Token);
                for (var wait = Stopwatch.StartNew(); (int)(await RunAsync(observer, "SELECT cardinality(pg_blocking_pids(@p))", ("p", pid)))! == 0;)
                {
                    Assert.True(wait.Elapsed < TimeSpan.FromSeconds(10), "The purge never blocked on the token row.");
                    await Task.Delay(25, Token);
                }

                await cancel.CancelAsync();
                var error = await Record.ExceptionAsync(() => purge);
                var clean = connection.State != System.Data.ConnectionState.Open || await NoTransactionOrLockAsync(connection);
                return (error, pid, clean);
            });

            Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.True(clean);
            Assert.Equal(0L, await RunAsync(observer, "SELECT count(*) FROM pg_locks WHERE pid = @p AND locktype = 'advisory'", ("p", pid)));
            var state = await RunAsync(observer, "SELECT state FROM pg_stat_activity WHERE pid = @p", ("p", pid)) as string;
            Assert.DoesNotContain("in transaction", state ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(3L, await AllFamiliesAsync(world, world.TenantA));
        }

        Assert.Equal(3, await PurgeAsync(world, world.TenantA));
    }

    // ---- Session settings and binding ----

    // Breaks when the batch timeouts are set for the session (set_config(..., false) or SET) instead of the transaction.
    [Fact]
    public async Task AfterAPurgeTheSessionsStatementAndLockTimeoutsAreWhatTheyWere()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await SeedEndedAsync(world, world.TenantA, 1);

        var (deleted, before, after) = await OnTenantConnectionAsync(world, world.TenantA, async (scope, connection) =>
        {
            const string Show = "SELECT current_setting('statement_timeout') || '|' || current_setting('lock_timeout')";
            await RunAsync(connection, "SET statement_timeout = '4321ms'; SET lock_timeout = '1234ms'");
            var before = (string)(await RunAsync(connection, Show))!;
            var count = await scope.GetRequiredService<RefreshTokenPurger>().PurgeCurrentTenantAsync(Token);
            return (count, before, (string)(await RunAsync(connection, Show))!);
        });

        Assert.Equal(1, deleted);
        Assert.Equal("4321ms|1234ms", before);
        Assert.Equal(before, after);
    }

    // The smallest number of days whose calendar span from now differs from 24 h per day in America/New_York (a DST change
    // lies in between); a day count always exists within a year, so these tests do not depend on today's date.
    private static async Task<int> DaysAcrossADstChangeAsync(NpgsqlConnection connection, string direction)
    {
        await RunAsync(connection, "SET TIME ZONE 'America/New_York'");
        return (int)(await RunAsync(connection, $"""
            SELECT min(n) FROM generate_series(1, 400) AS n
            WHERE now() {direction} make_interval(days => n) <> now() {direction} make_interval(hours => 24 * n)
            """))!;
    }

    // Breaks when GracePeriod reaches PostgreSQL as days (calendar arithmetic in the session time zone): across a DST
    // change the cut-off moves by an hour, deleting the family 30 min within the grace or keeping the one 30 min past it.
    [Fact]
    public async Task UnderANewYorkSessionTheGracePeriodIsExactElapsedTimeAcrossADstChange()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var (within, past) = (Guid.NewGuid(), Guid.NewGuid());

        var (deleted, zone) = await OnTenantConnectionAsync(world, world.TenantA, async (scope, connection) =>
        {
            var days = await DaysAcrossADstChangeAsync(connection, "-");
            await RunAsync(connection, """
                INSERT INTO refresh_token_families (id, user_id, created_at, absolute_expires_at, revoked_at)
                SELECT id, gen_random_uuid(), now() - interval '1000 days', now() + interval '1 day',
                       now() - make_interval(hours => 24 * @n) + shift
                FROM (VALUES (@within, interval '30 minutes'), (@past, interval '-30 minutes')) AS v(id, shift)
                """, ("n", days), ("within", within), ("past", past));
            var purger = ActivatorUtilities.CreateInstance<RefreshTokenPurger>(
                scope, Options.Create(new RefreshTokenPurgeOptions { GracePeriod = TimeSpan.FromDays(days) }));
            var count = await purger.PurgeCurrentTenantAsync(Token);
            return (count, (string)(await RunAsync(connection, "SELECT current_setting('TimeZone')"))!);
        });

        Assert.Equal("America/New_York", zone);
        Assert.Equal(1, deleted);
        Assert.Equal(1L, await FamiliesAsync(world, world.TenantA, within));
        Assert.Equal(0L, await FamiliesAsync(world, world.TenantA, past));
    }

    // Breaks when AbsoluteLifetime reaches PostgreSQL as days: across a DST change the session would last an hour more or less.
    [Fact]
    public async Task UnderANewYorkSessionANewFamilysAbsoluteLifetimeIsExactElapsedTimeAcrossADstChange()
    {
        using var world = await AccessWorld.StartAsync(postgres);

        var (days, seconds) = await OnTenantConnectionAsync(world, world.TenantA, async (scope, connection) =>
        {
            var days = await DaysAcrossADstChangeAsync(connection, "+");
            var store = ActivatorUtilities.CreateInstance<DatabaseRefreshTokenStore>(
                scope, Options.Create(new RefreshTokenOptions { AbsoluteLifetime = TimeSpan.FromDays(days) }));
            await store.SaveAsync(Id(Guid.NewGuid()), Id(world.TenantA), NewToken(), DateTime.UtcNow.AddDays(1), null, "stamp", Token);
            var seconds = (decimal)(await RunAsync(connection,
                "SELECT extract(epoch FROM absolute_expires_at) - extract(epoch FROM created_at) FROM refresh_token_families"))!;
            return (days, seconds);
        });

        Assert.Equal(86_400m * days, seconds);
    }

    // Breaks when the purge is not refused (InvalidOperationException) on a connection whose search_path names pg_temp
    // first, so current_schema() is pg_temp_N and a temp refresh_token_families shadows the tenant's, or when it deletes
    // from either table.
    [Fact]
    public async Task APurgeOnAConnectionWhoseCurrentSchemaIsNotTheTenantsThrowsAndDeletesNothing()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await SeedEndedAsync(world, world.TenantA, 3);
        var schema = TenantSchemaName.For(world.TenantA);

        var (error, inTemp, inTenant) = await OnTenantConnectionAsync(world, world.TenantA, async (scope, connection) =>
        {
            await RunAsync(connection, "CREATE TEMP TABLE refresh_token_families AS SELECT * FROM refresh_token_families");
            await RunAsync(connection, "SELECT set_config('search_path', 'pg_temp, ' || quote_ident(@s) || ', extensions', false)", ("s", schema));
            Assert.StartsWith("pg_temp", (string)(await RunAsync(connection, "SELECT current_schema()"))!, StringComparison.Ordinal);
            var error = await Record.ExceptionAsync(() => scope.GetRequiredService<RefreshTokenPurger>().PurgeCurrentTenantAsync(Token));
            return (error,
                (long)(await RunAsync(connection, "SELECT count(*) FROM pg_temp.refresh_token_families"))!,
                (long)(await RunAsync(connection, $"SELECT count(*) FROM \"{schema}\".refresh_token_families"))!);
        });

        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(3L, inTemp);
        Assert.Equal(3L, inTenant);
        Assert.Equal(3L, await AllFamiliesAsync(world, world.TenantA));
    }

    // Breaks when the binding check trusts current_schema() (still the tenant's here) instead of where the unqualified
    // table resolves: pg_temp, not named in search_path, is searched first implicitly, so the temp table shadows the
    // tenant's; the purge must refuse before deleting from either table.
    [Fact]
    public async Task APurgeWhereAnImplicitlySearchedTempTableShadowsTheTenantsThrowsAndDeletesNothing()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await SeedEndedAsync(world, world.TenantA, 3);
        var schema = TenantSchemaName.For(world.TenantA);

        var (error, current, inTemp, inTenant) = await OnTenantConnectionAsync(world, world.TenantA, async (scope, connection) =>
        {
            await RunAsync(connection, "CREATE TEMP TABLE refresh_token_families AS SELECT * FROM refresh_token_families");
            await RunAsync(connection, "SELECT set_config('search_path', quote_ident(@s) || ', extensions', false)", ("s", schema));
            var current = (string)(await RunAsync(connection, "SELECT current_schema()"))!;
            Assert.True((bool)(await RunAsync(connection, "SELECT relnamespace = pg_my_temp_schema() FROM pg_class WHERE oid = 'refresh_token_families'::regclass"))!);
            var error = await Record.ExceptionAsync(() => scope.GetRequiredService<RefreshTokenPurger>().PurgeCurrentTenantAsync(Token));
            return (error, current,
                (long)(await RunAsync(connection, "SELECT count(*) FROM pg_temp.refresh_token_families"))!,
                (long)(await RunAsync(connection, $"SELECT count(*) FROM \"{schema}\".refresh_token_families"))!);
        });

        Assert.Equal(schema, current);
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(3L, inTemp);
        Assert.Equal(3L, inTenant);
        Assert.Equal(3L, await AllFamiliesAsync(world, world.TenantA));
    }
}
