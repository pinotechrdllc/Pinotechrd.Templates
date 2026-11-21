using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.Modules.Access.Infrastructure.Persistence;
using MyApp.Modules.Access.Infrastructure.Security;
using Npgsql;
using Pinotechrd.Security.Interfaces;

namespace MyApp.Api.Tests.Access;

// The refresh token purger of the Access module on a real PostgreSQL database with two tenant schemas, A and B. Families
// are started through the store the host registers and aged with SQL in the tenant's own schema (or bulk-inserted there
// already aged); the purger is resolved from a scope whose tenant is set, the way a caller does it. The
// grace period is the default (7 days) unless a test says otherwise.
[Collection(PostgresContainer.Name)]
public partial class RefreshTokenPurgerTests(PostgresFixture postgres)
{
    private const string Purge = RefreshTokenPurgeOptions.SectionName + ":";

    private const string EndedLongAgo = "absolute_expires_at = now() - interval '8 days'";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string NewToken() => "rt-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static string Id(Guid id) => id.ToString("D");

    private static async Task<T> InTenantAsync<T>(AccessWorld world, Guid tenant, Func<IServiceProvider, Task<T>> work)
    {
        T result = default!;
        await AccessDatabase.InTenantAsync(world.Services, tenant, async scope => result = await work(scope));
        return result;
    }

    private static Task<T> StoreAsync<T>(AccessWorld world, Guid tenant, Func<IRefreshTokenStore, Task<T>> work) =>
        InTenantAsync(world, tenant, scope => work(scope.GetRequiredService<IRefreshTokenStore>()));

    private static Task<int> PurgeAsync(AccessWorld world, Guid tenant) =>
        InTenantAsync(world, tenant, scope => scope.GetRequiredService<RefreshTokenPurger>().PurgeCurrentTenantAsync(Token));

    private static Task<RefreshTokenConsumeStatus> ConsumeAsync(AccessWorld world, Guid tenant, string token) =>
        StoreAsync(world, tenant, async s => (await s.ConsumeAsync(token, Id(tenant), Token)).Status);

    private static Task<object?> ScalarAsync(AccessWorld world, Guid tenant, string sql, params (string Name, object Value)[] parameters) =>
        InTenantAsync(world, tenant, async scope =>
        {
            var connection = await scope.GetRequiredService<AccessDbContext>().Database.OpenTenantConnectionAsync(Token);
            await using var command = new NpgsqlCommand(sql, connection);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            var result = await command.ExecuteScalarAsync(Token);
            return result is DBNull ? null : result;
        });

    private static async Task<long> CountAsync(AccessWorld world, Guid tenant, string sql, params (string Name, object Value)[] parameters) =>
        (long)(await ScalarAsync(world, tenant, sql, parameters))!;

    private static Task<long> FamiliesAsync(AccessWorld world, Guid tenant, Guid family) =>
        CountAsync(world, tenant, "SELECT count(*) FROM refresh_token_families WHERE id = @f", ("f", family));

    private static Task<long> TokensAsync(AccessWorld world, Guid tenant, Guid family) =>
        CountAsync(world, tenant, "SELECT count(*) FROM refresh_tokens WHERE family_id = @f", ("f", family));

    // A family started through the store: its first token consumed, a rotated one live; then aged with SQL (a SET clause).
    private static async Task<(string Consumed, string Live, Guid Family)> FamilyAsync(AccessWorld world, Guid tenant, string? age = null)
    {
        var user = Id(Guid.NewGuid());
        var first = NewToken();
        var next = NewToken();
        await StoreAsync(world, tenant, async s => { await s.SaveAsync(user, Id(tenant), first, DateTime.UtcNow.AddDays(7), null, "stamp", Token); return 0; });
        var consumed = await StoreAsync(world, tenant, s => s.ConsumeAsync(first, Id(tenant), Token));
        Assert.Equal(RefreshTokenConsumeStatus.Consumed, consumed.Status);
        await StoreAsync(world, tenant, async s => { await s.SaveAsync(user, Id(tenant), next, DateTime.UtcNow.AddDays(7), consumed.FamilyId, "stamp", Token); return 0; });
        var family = Guid.Parse(consumed.FamilyId!);
        if (age is not null)
        {
            await ScalarAsync(world, tenant, $"UPDATE refresh_token_families SET {age} WHERE id = @f", ("f", family));
        }

        return (first, next, family);
    }

    // Inserts count families (one token each) that ended 30 days ago, far past the grace period.
    private static async Task SeedEndedAsync(AccessWorld world, Guid tenant, int count) =>
        await ScalarAsync(world, tenant, """
            WITH f AS (
                INSERT INTO refresh_token_families (id, user_id, created_at, absolute_expires_at)
                SELECT gen_random_uuid(), gen_random_uuid(), now() - interval '60 days', now() - interval '30 days' FROM generate_series(1, @n)
                RETURNING id, user_id)
            INSERT INTO refresh_tokens (id, family_id, token_hash, user_id, created_at, expires_at)
            SELECT gen_random_uuid(), f.id, uuid_send(gen_random_uuid()), f.user_id, now() - interval '60 days', now() - interval '30 days' FROM f
            """, ("n", count));

    private static Task<long> AllFamiliesAsync(AccessWorld world, Guid tenant) => CountAsync(world, tenant, "SELECT count(*) FROM refresh_token_families");

    private static Task<long> AllTokensAsync(AccessWorld world, Guid tenant) => CountAsync(world, tenant, "SELECT count(*) FROM refresh_tokens");

    // Runs every piece of work on its own thread, released together.
    private static async Task<T[]> TogetherAsync<T>(params Func<Task<T>>[] work)
    {
        using var gate = new ManualResetEventSlim(false);
        var tasks = work.Select(w => Task.Run(() => { gate.Wait(Token); return w(); }, Token)).ToArray();
        gate.Set();
        return await Task.WhenAll(tasks);
    }

    // ---- What is deleted, what survives ----

    // Breaks when a family past its absolute expiry plus the grace, or revoked longer than the grace ago, is kept, or when
    // its tokens outlive it (the purge must delete the family and the cascade every token, consumed or not).
    [Theory]
    [InlineData(EndedLongAgo)]
    [InlineData("revoked_at = now() - interval '8 days'")]
    public async Task AFamilyEndedOrRevokedLongerThanTheGraceAgoIsDeletedWithAllItsTokens(string age)
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var (_, _, family) = await FamilyAsync(world, world.TenantA, age);
        Assert.Equal(2L, await TokensAsync(world, world.TenantA, family));

        var deleted = await PurgeAsync(world, world.TenantA);

        Assert.Equal(1, deleted);
        Assert.Equal(0L, await FamiliesAsync(world, world.TenantA, family));
        Assert.Equal(0L, await TokensAsync(world, world.TenantA, family));
    }

    // Breaks when the purge deletes a live family (or its consumed tokens, which reuse detection needs), a family revoked
    // within the grace, or one whose absolute expiry passed within the grace.
    [Theory]
    [InlineData(null)]
    [InlineData("revoked_at = now() - interval '1 day'")]
    [InlineData("absolute_expires_at = now() - interval '6 days'")]
    public async Task ALiveFamilyOrOneWithinTheGraceSurvivesWithAllItsTokens(string? age)
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var (_, _, family) = await FamilyAsync(world, world.TenantA, age);
        var (_, _, ended) = await FamilyAsync(world, world.TenantA, EndedLongAgo);

        var deleted = await PurgeAsync(world, world.TenantA);

        Assert.Equal(1, deleted);
        Assert.Equal(0L, await FamiliesAsync(world, world.TenantA, ended));
        Assert.Equal(1L, await FamiliesAsync(world, world.TenantA, family));
        Assert.Equal(2L, await TokensAsync(world, world.TenantA, family));
        Assert.Equal(1L, await CountAsync(world, world.TenantA, "SELECT count(*) FROM refresh_tokens WHERE family_id = @f AND consumed_at IS NOT NULL", ("f", family)));
    }

    // Breaks when a purge loses the consumed tokens of a live family: their replay must still be Reused and end the family.
    [Fact]
    public async Task AfterAPurgeAReplayedTokenOfALiveFamilyIsStillReusedAndRevokesTheFamily()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var (consumed, live, _) = await FamilyAsync(world, world.TenantA);
        await FamilyAsync(world, world.TenantA, EndedLongAgo);
        Assert.Equal(1, await PurgeAsync(world, world.TenantA));

        Assert.Equal(RefreshTokenConsumeStatus.Reused, await ConsumeAsync(world, world.TenantA, consumed));
        Assert.Equal(RefreshTokenConsumeStatus.Revoked, await ConsumeAsync(world, world.TenantA, live));
    }

    // Breaks when a token of a purged family is still honoured, or when presenting it brings a family back.
    [Fact]
    public async Task ATokenOfAPurgedFamilyIsNotFoundAndNoFamilyIsRecreated()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var (consumed, live, family) = await FamilyAsync(world, world.TenantA, EndedLongAgo);
        Assert.Equal(1, await PurgeAsync(world, world.TenantA));

        Assert.Equal(RefreshTokenConsumeStatus.NotFound, await ConsumeAsync(world, world.TenantA, live));
        Assert.Equal(RefreshTokenConsumeStatus.NotFound, await ConsumeAsync(world, world.TenantA, consumed));
        Assert.Equal(0L, await FamiliesAsync(world, world.TenantA, family));
        Assert.Equal(0L, await AllFamiliesAsync(world, world.TenantA));
        Assert.Equal(0L, await AllTokensAsync(world, world.TenantA));
    }

    // ---- Tenants ----

    // Breaks when the purge of one tenant reaches another tenant's schema: B's rows, purgeable ones included, are untouched.
    [Fact]
    public async Task PurgingTenantALeavesTheSameRowsOfTenantBUntouched()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        foreach (var tenant in new[] { world.TenantA, world.TenantB })
        {
            await FamilyAsync(world, tenant);
            await FamilyAsync(world, tenant, EndedLongAgo);
            await SeedEndedAsync(world, tenant, 7);
        }

        const string Snapshot = """
            SELECT (SELECT string_agg(f::text, '|' ORDER BY f.id) FROM refresh_token_families f) || '#' ||
                   (SELECT string_agg(t::text, '|' ORDER BY t.id) FROM refresh_tokens t)
            """;
        var before = await ScalarAsync(world, world.TenantB, Snapshot);

        var deleted = await PurgeAsync(world, world.TenantA);

        Assert.Equal(8, deleted);
        Assert.Equal(1L, await AllFamiliesAsync(world, world.TenantA));
        Assert.Equal(before, await ScalarAsync(world, world.TenantB, Snapshot));
        Assert.Equal(9L, await AllFamiliesAsync(world, world.TenantB));
    }

    // Breaks when the purge's connection path runs as the owner or a superuser: on the connection of a scope for A, SQL
    // qualified with B's schema must be refused by PostgreSQL (42501), and the connection must run as A's tenant role.
    [Fact]
    public async Task OnThePurgeConnectionOfTenantASchemaQualifiedSqlOnTenantBIsRefusedAndRunsAsTenantAsRole()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await FamilyAsync(world, world.TenantB, EndedLongAgo);
        var schemaB = TenantSchemaName.For(world.TenantB);
        var expectedRole = TenantRoles.For(world.Services.GetRequiredService<NpgsqlDataSource>().ConnectionString, world.TenantA);

        var (user, sessionUser, states) = await InTenantAsync(world, world.TenantA, async scope =>
        {
            var connection = await scope.GetRequiredService<AccessDbContext>().Database.OpenTenantConnectionAsync(Token);
            await using var who = new NpgsqlCommand("SELECT current_user || '|' || session_user", connection);
            var parts = ((string)(await who.ExecuteScalarAsync(Token))!).Split('|');
            var refused = new List<string>();
            foreach (var sql in new[]
            {
                $"SELECT count(*) FROM \"{schemaB}\".refresh_token_families",
                $"DELETE FROM \"{schemaB}\".refresh_token_families WHERE absolute_expires_at < now()",
            })
            {
                await using var attack = new NpgsqlCommand(sql, connection);
                var error = await Assert.ThrowsAsync<PostgresException>(() => attack.ExecuteNonQueryAsync(Token));
                refused.Add(error.SqlState);
            }

            return (parts[0], parts[1], refused);
        });

        Assert.Equal(2, states.Count);
        Assert.All(states, state => Assert.Equal("42501", state));
        Assert.Equal(expectedRole, user);
        Assert.NotEqual(sessionUser, user);
        Assert.Equal(1L, await AllFamiliesAsync(world, world.TenantB));
    }

    // ---- Batches ----

    // Breaks when the purge stops after one batch, or does not stop after MaxBatchesPerTenant batches of BatchSize.
    [Theory]
    [InlineData(50, 23, 0)]
    [InlineData(2, 10, 13)]
    public async Task MoreRowsThanOneBatchAreDeletedAcrossBatchesUpToTheBatchLimit(int maxBatches, int expectedDeleted, long expectedLeft)
    {
        using var world = await AccessWorld.StartAsync(postgres, f => f
            .WithSetting(Purge + "BatchSize", "5")
            .WithSetting(Purge + "MaxBatchesPerTenant", maxBatches.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await SeedEndedAsync(world, world.TenantA, 23);

        var deleted = await PurgeAsync(world, world.TenantA);

        Assert.Equal(expectedDeleted, deleted);
        Assert.Equal(expectedLeft, await AllFamiliesAsync(world, world.TenantA));
        Assert.Equal(expectedLeft, await AllTokensAsync(world, world.TenantA));
    }

    // ---- Concurrency ----

    // Breaks when two instances purging one tenant together deadlock (40P01), throw, double-count or leave rows behind:
    // together they delete every target family exactly once.
    [Fact]
    public async Task TwoConcurrentPurgesOfOneTenantDeleteEveryTargetFamilyExactlyOnceWithoutError()
    {
        using var world = await AccessWorld.StartAsync(postgres, f => f
            .WithSetting(Purge + "BatchSize", "5")
            .WithSetting(Purge + "MaxBatchesPerTenant", "1000"));
        await SeedEndedAsync(world, world.TenantA, 300);
        var (_, _, live) = await FamilyAsync(world, world.TenantA);

        var results = await TogetherAsync(() => PurgeAsync(world, world.TenantA), () => PurgeAsync(world, world.TenantA));

        Assert.Equal(300, results.Sum());
        Assert.Equal(1L, await AllFamiliesAsync(world, world.TenantA));
        Assert.Equal(1L, await FamiliesAsync(world, world.TenantA, live));
        Assert.Equal(2L, await AllTokensAsync(world, world.TenantA));
    }

    // Breaks when the purge deadlocks or blocks against refresh, save and logout traffic on live families, or deletes one
    // of them: every rotation succeeds, every live family remains (logged-out ones are within the grace) and every target goes.
    [Fact]
    public async Task APurgeDuringRefreshAndLogoutTrafficLosesNoLiveFamilyAndDeadlocksNothing()
    {
        using var world = await AccessWorld.StartAsync(postgres, f => f
            .WithSetting(Purge + "BatchSize", "5")
            .WithSetting(Purge + "MaxBatchesPerTenant", "1000"));
        await SeedEndedAsync(world, world.TenantA, 300);
        var families = new List<(string Live, Guid Family)>();
        for (var i = 0; i < 8; i++)
        {
            var (_, live, family) = await FamilyAsync(world, world.TenantA);
            families.Add((live, family));
        }

        // Each session refreshes five times (consume, then save the rotated token), and every other one then logs out.
        Func<Task<int>> Traffic(int index) => async () =>
        {
            var (token, _) = families[index];
            for (var round = 0; round < 5; round++)
            {
                var consumed = await StoreAsync(world, world.TenantA, s => s.ConsumeAsync(token, Id(world.TenantA), Token));
                Assert.Equal(RefreshTokenConsumeStatus.Consumed, consumed.Status);
                var next = NewToken();
                await StoreAsync(world, world.TenantA, async s =>
                {
                    await s.SaveAsync(consumed.UserId!, Id(world.TenantA), next, DateTime.UtcNow.AddDays(7), consumed.FamilyId, null, Token);
                    return 0;
                });
                token = next;
            }

            if (index % 2 == 0)
            {
                await StoreAsync(world, world.TenantA, async s => { await s.RevokeAsync(token, Token); return 0; });
            }

            return 0;
        };

        var results = await TogetherAsync([() => PurgeAsync(world, world.TenantA), .. Enumerable.Range(0, families.Count).Select(Traffic)]);

        Assert.Equal(300, results[0]);
        Assert.Equal(families.Count, await AllFamiliesAsync(world, world.TenantA));
        foreach (var (_, family) in families)
        {
            Assert.Equal(1L, await FamiliesAsync(world, world.TenantA, family));
            Assert.Equal(7L, await TokensAsync(world, world.TenantA, family));
        }
    }
}
