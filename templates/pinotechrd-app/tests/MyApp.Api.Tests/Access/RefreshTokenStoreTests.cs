using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.Modules.Access.Infrastructure.Persistence;
using MyApp.Modules.Access.Infrastructure.Security;
using Npgsql;
using Pinotechrd.Security.Interfaces;

namespace MyApp.Api.Tests.Access;

// The refresh token store of the Access module on a real PostgreSQL database with two tenant schemas, A and B. The
// store is the scoped one the host registers for the library's IRefreshTokenStore; rows are read and aged with SQL in the
// tenant's own schema, the way an operator would, never through the store.
[Collection(PostgresContainer.Name)]
public class RefreshTokenStoreTests(PostgresFixture postgres)
{
    private static string NewToken() => "rt-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static DateTime Sliding => DateTime.UtcNow.AddDays(7);

    private static string Id(Guid id) => id.ToString("D");

    private static async Task<T> WithStoreAsync<T>(AccessWorld world, Guid tenant, Func<IRefreshTokenStore, Task<T>> work)
    {
        T result = default!;
        await AccessDatabase.InTenantAsync(world.Services, tenant, async scope => result = await work(scope.GetRequiredService<IRefreshTokenStore>()));
        return result;
    }

    private static Task WithStoreAsync(AccessWorld world, Guid tenant, Func<IRefreshTokenStore, Task> work) =>
        AccessDatabase.InTenantAsync(world.Services, tenant, scope => work(scope.GetRequiredService<IRefreshTokenStore>()));

    private static async Task<object?> ScalarAsync(AccessWorld world, Guid tenant, string sql, params (string Name, object Value)[] parameters)
    {
        object? result = null;
        await AccessDatabase.InTenantAsync(world.Services, tenant, async scope =>
        {
            var db = scope.GetRequiredService<AccessDbContext>();
            var connection = await db.Database.OpenTenantConnectionAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        });
        return result is DBNull ? null : result;
    }

    private static async Task ExecuteAsync(AccessWorld world, Guid tenant, string sql, params (string Name, object Value)[] parameters)
    {
        await AccessDatabase.InTenantAsync(world.Services, tenant, async scope =>
        {
            var db = scope.GetRequiredService<AccessDbContext>();
            var connection = await db.Database.OpenTenantConnectionAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        });
    }

    private static byte[] HashOf(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    // Starts a family with one token and returns the token and the family (read from the table: a consume would burn the token).
    private static async Task<(string Token, string Family)> StartFamilyAsync(
        AccessWorld world, Guid tenant, Guid user, DateTime? expires = null, string? stamp = "stamp-1")
    {
        var token = NewToken();
        await WithStoreAsync(world, tenant, store =>
            store.SaveAsync(Id(user), Id(tenant), token, expires ?? Sliding, null, stamp, TestContext.Current.CancellationToken));
        var family = (Guid)(await ScalarAsync(world, tenant, "SELECT family_id FROM refresh_tokens WHERE token_hash = @h", ("h", HashOf(token))))!;
        return (token, Id(family));
    }

    private static Task<RefreshTokenConsumeResult> ConsumeAsync(AccessWorld world, Guid tenant, string token, Guid? expectedTenant = null) =>
        WithStoreAsync(world, tenant, store => store.ConsumeAsync(token, Id(expectedTenant ?? tenant), TestContext.Current.CancellationToken));

    private static Task<RefreshTokenValidationResult> ValidateAsync(AccessWorld world, Guid tenant, string token) =>
        WithStoreAsync(world, tenant, store => store.ValidateAsync(token, TestContext.Current.CancellationToken));

    private static Task SaveIntoAsync(AccessWorld world, Guid tenant, Guid user, string token, string family, DateTime? expires = null) =>
        WithStoreAsync(world, tenant, store =>
            store.SaveAsync(Id(user), Id(tenant), token, expires ?? Sliding, family, null, TestContext.Current.CancellationToken));

    // ---- Registration ----

    // Breaks when the host keeps the library's in-memory singleton: tokens would not survive a restart or reach other instances.
    [Fact]
    public async Task TheHostResolvesTheDatabaseStoreAsTheRefreshTokenStoreAndOneNewInstancePerScope()
    {
        using var world = await AccessWorld.StartAsync(postgres);

        await using var first = world.Services.CreateAsyncScope();
        await using var second = world.Services.CreateAsyncScope();
        AccessDatabase.EnterTenant(first.ServiceProvider, world.TenantA);
        AccessDatabase.EnterTenant(second.ServiceProvider, world.TenantA);
        var a = first.ServiceProvider.GetRequiredService<IRefreshTokenStore>();
        var again = first.ServiceProvider.GetRequiredService<IRefreshTokenStore>();
        var b = second.ServiceProvider.GetRequiredService<IRefreshTokenStore>();

        Assert.IsType<DatabaseRefreshTokenStore>(a);
        Assert.Same(a, again);
        Assert.NotSame(a, b);
    }

    // Breaks when a token only lives in the process: a store of another scope (another request) must find it.
    [Fact]
    public async Task ATokenSavedInOneScopeIsConsumedFromAnotherScope()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var (token, _) = await StartFamilyAsync(world, world.TenantA, Guid.NewGuid());

        var result = await ConsumeAsync(world, world.TenantA, token);

        Assert.Equal(RefreshTokenConsumeStatus.Consumed, result.Status);
    }

    // ---- Save, consume, reuse ----

    // Breaks when the consume does not return what the token was saved with (user, tenant, family, security stamp).
    [Fact]
    public async Task ConsumingASavedTokenReturnsItsUserTenantFamilyAndSecurityStamp()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = Guid.NewGuid();
        var (token, family) = await StartFamilyAsync(world, world.TenantA, user, stamp: "the-stamp");

        var result = await ConsumeAsync(world, world.TenantA, token);

        Assert.Equal(RefreshTokenConsumeStatus.Consumed, result.Status);
        Assert.True(result.Succeeded);
        Assert.Equal(Id(user), result.UserId);
        Assert.Equal(Id(world.TenantA), result.TenantId);
        Assert.Equal(family, result.FamilyId);
        Assert.Equal("the-stamp", result.SecurityStamp);
    }

    // Breaks when a token saved with no security stamp comes back with an invented one.
    [Fact]
    public async Task ATokenSavedWithoutASecurityStampIsConsumedWithNone()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var (token, _) = await StartFamilyAsync(world, world.TenantA, Guid.NewGuid(), stamp: null);

        var result = await ConsumeAsync(world, world.TenantA, token);

        Assert.Equal(RefreshTokenConsumeStatus.Consumed, result.Status);
        Assert.Null(result.SecurityStamp);
    }

    // Breaks when a consumed token can be consumed again, when reuse does not revoke the family, or when a sibling stays usable.
    [Fact]
    public async Task ReusingAConsumedTokenIsReusedAndRevokesTheWholeFamilyWhoseSiblingIsThenRevoked()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = Guid.NewGuid();
        var (first, family) = await StartFamilyAsync(world, world.TenantA, user);
        var sibling = NewToken();
        await SaveIntoAsync(world, world.TenantA, user, sibling, family);

        var firstConsume = await ConsumeAsync(world, world.TenantA, first);
        var reuse = await ConsumeAsync(world, world.TenantA, first);
        var siblingConsume = await ConsumeAsync(world, world.TenantA, sibling);

        Assert.Equal(RefreshTokenConsumeStatus.Consumed, firstConsume.Status);
        Assert.Equal(RefreshTokenConsumeStatus.Reused, reuse.Status);
        Assert.Equal(RefreshTokenConsumeStatus.Revoked, siblingConsume.Status);
    }

    // Breaks when a rotation (a token saved into the family of the consumed one) does not keep the family.
    [Fact]
    public async Task ARotatedTokenJoinsTheFamilyOfTheConsumedOne()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = Guid.NewGuid();
        var (first, family) = await StartFamilyAsync(world, world.TenantA, user);
        var consumed = await ConsumeAsync(world, world.TenantA, first);
        var next = NewToken();
        await SaveIntoAsync(world, world.TenantA, user, next, consumed.FamilyId!);

        var result = await ConsumeAsync(world, world.TenantA, next);

        Assert.Equal(RefreshTokenConsumeStatus.Consumed, result.Status);
        Assert.Equal(family, result.FamilyId);
    }

    // Breaks when a token that was never saved is anything but NotFound (empty included).
    [Theory]
    [InlineData("never-saved-token")]
    [InlineData("")]
    public async Task AnUnknownOrEmptyTokenIsNotFound(string token)
    {
        using var world = await AccessWorld.StartAsync(postgres);

        var result = await ConsumeAsync(world, world.TenantA, token);

        Assert.Equal(RefreshTokenConsumeStatus.NotFound, result.Status);
    }

    // ---- Concurrency ----

    // Breaks when the consume is a read then a write: two requests presenting one token could both be given a session.
    // Fifty rounds, each with two scopes (two connections) consuming at the same moment: exactly one wins.
    [Fact]
    public async Task OfTwoConcurrentConsumesOfOneTokenExactlyOneSucceedsInFiftyRuns()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = Guid.NewGuid();
        for (var run = 0; run < 50; run++)
        {
            var (token, _) = await StartFamilyAsync(world, world.TenantA, user);
            using var gate = new ManualResetEventSlim(false);

            Task<RefreshTokenConsumeResult> Racer() => Task.Run(() =>
            {
                gate.Wait(TestContext.Current.CancellationToken);
                return ConsumeAsync(world, world.TenantA, token);
            }, TestContext.Current.CancellationToken);

            var one = Racer();
            var two = Racer();
            gate.Set();
            var results = await Task.WhenAll(one, two);

            Assert.True(
                results.Count(r => r.Status == RefreshTokenConsumeStatus.Consumed) == 1,
                $"Run {run}: {results[0].Status}, {results[1].Status}");
            Assert.DoesNotContain(
                results,
                r => r.Status is RefreshTokenConsumeStatus.NotFound or RefreshTokenConsumeStatus.Expired or RefreshTokenConsumeStatus.TenantMismatch);
        }
    }

    // ---- Tenants ----

    // Breaks when the tables are shared between tenants or the lookup ignores the schema: B must not see A's token, and A's
    // token must stay consumable in A.
    [Fact]
    public async Task ATokenSavedInTenantAIsNotFoundFromTenantBAndStillConsumableInA()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var (token, _) = await StartFamilyAsync(world, world.TenantA, Guid.NewGuid());

        var fromB = await ConsumeAsync(world, world.TenantB, token);
        var validateB = await ValidateAsync(world, world.TenantB, token);
        var fromA = await ConsumeAsync(world, world.TenantA, token);

        Assert.Equal(RefreshTokenConsumeStatus.NotFound, fromB.Status);
        Assert.False(validateB.IsValid);
        Assert.Equal(RefreshTokenConsumeStatus.Consumed, fromA.Status);
    }

    // Breaks when a request resolved for another tenant can use or burn the token: it is a TenantMismatch and nothing changes.
    [Fact]
    public async Task ATokenPresentedForAnotherTenantIsATenantMismatchAndIsLeftUnchanged()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var (token, _) = await StartFamilyAsync(world, world.TenantA, Guid.NewGuid());

        var mismatch = await ConsumeAsync(world, world.TenantA, token, expectedTenant: world.TenantB);
        var afterwards = await ConsumeAsync(world, world.TenantA, token);

        Assert.Equal(RefreshTokenConsumeStatus.TenantMismatch, mismatch.Status);
        Assert.Equal(RefreshTokenConsumeStatus.Consumed, afterwards.Status);
    }

    // Breaks when an unknown token is told apart from a known one by a mismatch (it would confirm a token exists elsewhere).
    [Fact]
    public async Task AnUnknownTokenPresentedForAnotherTenantIsNotFound()
    {
        using var world = await AccessWorld.StartAsync(postgres);

        var result = await ConsumeAsync(world, world.TenantA, "never-saved-token", expectedTenant: world.TenantB);

        Assert.Equal(RefreshTokenConsumeStatus.NotFound, result.Status);
    }

    // Breaks when a token can be written for a tenant other than the scope's (it would land in the wrong schema).
    [Fact]
    public async Task SavingATokenForATenantOtherThanTheScopesThrowsAndStoresNothing()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var token = NewToken();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WithStoreAsync(world, world.TenantA, s => s.SaveAsync(Id(Guid.NewGuid()), Id(world.TenantB), token, Sliding, null, null, TestContext.Current.CancellationToken)));

        Assert.Equal(0L, await ScalarAsync(world, world.TenantA, "SELECT count(*) FROM refresh_tokens"));
        Assert.Equal(0L, await ScalarAsync(world, world.TenantB, "SELECT count(*) FROM refresh_tokens"));
    }

    // ---- Expiry ----

    // Breaks when an expired token is consumable, or is reported as anything but Expired. Validate agrees and changes nothing.
    [Fact]
    public async Task ATokenPastItsExpiryIsExpiredAndNeverConsumed()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var (token, _) = await StartFamilyAsync(world, world.TenantA, Guid.NewGuid(), expires: DateTime.UtcNow.AddMinutes(-1));

        var validate = await ValidateAsync(world, world.TenantA, token);
        var first = await ConsumeAsync(world, world.TenantA, token);
        var second = await ConsumeAsync(world, world.TenantA, token);

        Assert.False(validate.IsValid);
        Assert.Equal(RefreshTokenConsumeStatus.Expired, first.Status);
        Assert.Equal(RefreshTokenConsumeStatus.Expired, second.Status);
        Assert.Null(await ScalarAsync(world, world.TenantA, "SELECT consumed_at FROM refresh_tokens WHERE token_hash = @h", ("h", HashOf(token))));
    }

    // Breaks when expiry is not judged by the database clock: a token consumable before its expiry is Expired once the row
    // is aged in SQL (no sleeping).
    [Fact]
    public async Task ATokenIsConsumableBeforeItsExpiryAndExpiredAfterByTheDatabaseClock()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var (soon, _) = await StartFamilyAsync(world, world.TenantA, Guid.NewGuid(), expires: DateTime.UtcNow.AddMinutes(1));
        var (aged, _) = await StartFamilyAsync(world, world.TenantA, Guid.NewGuid(), expires: DateTime.UtcNow.AddMinutes(1));
        await ExecuteAsync(world, world.TenantA, "UPDATE refresh_tokens SET expires_at = now() - interval '1 second' WHERE token_hash = @h", ("h", HashOf(aged)));

        var before = await ConsumeAsync(world, world.TenantA, soon);
        var after = await ConsumeAsync(world, world.TenantA, aged);

        Assert.Equal(RefreshTokenConsumeStatus.Consumed, before.Status);
        Assert.Equal(RefreshTokenConsumeStatus.Expired, after.Status);
    }

    // Breaks when the absolute cap is not enforced on consume: a family past its cap is Expired though the token's own
    // sliding expiry is still in the future.
    [Fact]
    public async Task AFamilyPastItsAbsoluteCapIsExpiredEvenWithAFreshSlidingExpiry()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var (first, family) = await StartFamilyAsync(world, world.TenantA, Guid.NewGuid());
        await ExecuteAsync(world, world.TenantA, "UPDATE refresh_token_families SET absolute_expires_at = now() - interval '1 hour' WHERE id = @f", ("f", Guid.Parse(family)));
        await ExecuteAsync(world, world.TenantA, "UPDATE refresh_tokens SET expires_at = now() + interval '7 days'");

        var validate = await ValidateAsync(world, world.TenantA, first);
        var result = await ConsumeAsync(world, world.TenantA, first);

        Assert.False(validate.IsValid);
        Assert.Equal(RefreshTokenConsumeStatus.Expired, result.Status);
    }

    // Breaks when a rotation into a family past its cap gets a fresh sliding expiry: the new token is Expired at once.
    [Fact]
    public async Task ATokenSavedIntoAFamilyPastItsCapIsExpiredDespiteAFreshSlidingExpiry()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = Guid.NewGuid();
        var (_, family) = await StartFamilyAsync(world, world.TenantA, user);
        await ExecuteAsync(world, world.TenantA, "UPDATE refresh_token_families SET absolute_expires_at = now() - interval '1 hour' WHERE id = @f", ("f", Guid.Parse(family)));
        var rotated = NewToken();
        await SaveIntoAsync(world, world.TenantA, user, rotated, family);

        var result = await ConsumeAsync(world, world.TenantA, rotated);

        Assert.Equal(RefreshTokenConsumeStatus.Expired, result.Status);
    }

    // Breaks when the stored expiry is the requested one instead of the lesser of it and the family's cap.
    [Fact]
    public async Task ATokenExpiryIsTheLesserOfTheRequestedOneAndTheFamilyCap()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = Guid.NewGuid();
        var (_, family) = await StartFamilyAsync(world, world.TenantA, user);
        await ExecuteAsync(world, world.TenantA, "UPDATE refresh_token_families SET absolute_expires_at = now() + interval '1 hour' WHERE id = @f", ("f", Guid.Parse(family)));
        var capped = NewToken();
        var shorter = NewToken();
        await SaveIntoAsync(world, world.TenantA, user, capped, family, DateTime.UtcNow.AddDays(7));
        await SaveIntoAsync(world, world.TenantA, user, shorter, family, DateTime.UtcNow.AddMinutes(5));

        var cappedExpiry = (DateTime)(await ScalarAsync(world, world.TenantA, "SELECT expires_at FROM refresh_tokens WHERE token_hash = @h", ("h", HashOf(capped))))!;
        var shorterExpiry = (DateTime)(await ScalarAsync(world, world.TenantA, "SELECT expires_at FROM refresh_tokens WHERE token_hash = @h", ("h", HashOf(shorter))))!;
        var cap = (DateTime)(await ScalarAsync(world, world.TenantA, "SELECT absolute_expires_at FROM refresh_token_families WHERE id = @f", ("f", Guid.Parse(family))))!;

        Assert.Equal(cap, cappedExpiry);
        Assert.InRange(shorterExpiry, DateTime.UtcNow.AddMinutes(4), DateTime.UtcNow.AddMinutes(6));
    }

    // Breaks when the family's cap is not the configured absolute lifetime from its creation, or is moved by later tokens.
    [Fact]
    public async Task AFamilyCapIsTheConfiguredAbsoluteLifetimeFromItsFirstTokenAndDoesNotMoveWithRotations()
    {
        using var world = await AccessWorld.StartAsync(postgres, f => f.WithSetting("Access:RefreshTokens:AbsoluteLifetime", "20.00:00:00"));
        var user = Guid.NewGuid();
        var (_, family) = await StartFamilyAsync(world, world.TenantA, user);
        const string CapSql = "SELECT absolute_expires_at FROM refresh_token_families WHERE id = @f";
        var span = (TimeSpan)(await ScalarAsync(world, world.TenantA, "SELECT absolute_expires_at - created_at FROM refresh_token_families WHERE id = @f", ("f", Guid.Parse(family))))!;
        var before = await ScalarAsync(world, world.TenantA, CapSql, ("f", Guid.Parse(family)));
        await SaveIntoAsync(world, world.TenantA, user, NewToken(), family);
        var after = await ScalarAsync(world, world.TenantA, CapSql, ("f", Guid.Parse(family)));

        Assert.Equal(TimeSpan.FromDays(20), span);
        Assert.Equal(before, after);
    }

    // ---- Storage ----

    // Breaks when the token is stored as text, or hashed with something other than SHA-256 of its UTF-8 bytes: the hash column
    // is exactly the 32-byte digest and the token text appears in no column of either table.
    [Fact]
    public async Task OnlyTheThirtyTwoByteSha256IsStoredAndTheTokenTextIsInNoColumnOfTheTables()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var token = NewToken();
        await WithStoreAsync(world, world.TenantA, s =>
            s.SaveAsync(Id(Guid.NewGuid()), Id(world.TenantA), token, Sliding, null, "stamp-x", TestContext.Current.CancellationToken));

        var stored = (byte[])(await ScalarAsync(world, world.TenantA, "SELECT token_hash FROM refresh_tokens"))!;
        var everything = (string)(await ScalarAsync(
            world,
            world.TenantA,
            "SELECT coalesce((SELECT string_agg(t::text, '|') FROM refresh_tokens t), '') || '|' || coalesce((SELECT string_agg(f::text, '|') FROM refresh_token_families f), '')"))!;

        Assert.Equal(32, stored.Length);
        Assert.Equal(HashOf(token), stored);
        Assert.DoesNotContain(token, everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToHexString(Encoding.UTF8.GetBytes(token)), everything, StringComparison.OrdinalIgnoreCase);
    }

    // Breaks when the same token can be stored twice (the second would reset or shadow the first).
    [Fact]
    public async Task SavingTheSameTokenTwiceThrowsInvalidOperationAndLeavesTheFirstRowAlone()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = Guid.NewGuid();
        var (token, _) = await StartFamilyAsync(world, world.TenantA, user);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WithStoreAsync(world, world.TenantA, s => s.SaveAsync(Id(user), Id(world.TenantA), token, Sliding, null, null, TestContext.Current.CancellationToken)));

        Assert.Equal(1L, await ScalarAsync(world, world.TenantA, "SELECT count(*) FROM refresh_tokens"));
        Assert.Equal(RefreshTokenConsumeStatus.Consumed, (await ConsumeAsync(world, world.TenantA, token)).Status);
    }

    // Breaks when saving a duplicate resets the state of a consumed token: a burnt token would be usable again.
    [Fact]
    public async Task SavingAConsumedTokenAgainDoesNotMakeItConsumableAgain()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = Guid.NewGuid();
        var (token, _) = await StartFamilyAsync(world, world.TenantA, user);
        await ConsumeAsync(world, world.TenantA, token);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WithStoreAsync(world, world.TenantA, s => s.SaveAsync(Id(user), Id(world.TenantA), token, Sliding, null, null, TestContext.Current.CancellationToken)));

        Assert.Equal(RefreshTokenConsumeStatus.Reused, (await ConsumeAsync(world, world.TenantA, token)).Status);
    }

    // Breaks when a user can attach a token to the family of another user (and so ride their session).
    [Fact]
    public async Task SavingIntoTheFamilyOfAnotherUserThrowsAndStoresNothing()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var (_, family) = await StartFamilyAsync(world, world.TenantA, Guid.NewGuid());
        var intruder = NewToken();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveIntoAsync(world, world.TenantA, Guid.NewGuid(), intruder, family));

        Assert.Equal(1L, await ScalarAsync(world, world.TenantA, "SELECT count(*) FROM refresh_tokens"));
        Assert.Equal(RefreshTokenConsumeStatus.NotFound, (await ConsumeAsync(world, world.TenantA, intruder)).Status);
    }

    // Breaks when an identifier that is not a GUID is accepted: the tables key users by GUID.
    [Fact]
    public async Task SavingWithAUserIdThatIsNotAGuidThrows()
    {
        using var world = await AccessWorld.StartAsync(postgres);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            WithStoreAsync(world, world.TenantA, s => s.SaveAsync("not-a-guid", Id(world.TenantA), NewToken(), Sliding, null, null, TestContext.Current.CancellationToken)));
    }

    // ---- Revocation ----

    // Breaks when a save that follows a revocation can be consumed: a revoked family stays revoked, and the token is stored revoked.
    [Fact]
    public async Task ATokenSavedIntoARevokedFamilyCanNeverBeConsumed()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = Guid.NewGuid();
        var (first, family) = await StartFamilyAsync(world, world.TenantA, user);
        await WithStoreAsync(world, world.TenantA, s => s.RevokeAsync(first, TestContext.Current.CancellationToken));
        var late = NewToken();
        await SaveIntoAsync(world, world.TenantA, user, late, family);

        var result = await ConsumeAsync(world, world.TenantA, late);
        var validate = await ValidateAsync(world, world.TenantA, late);

        Assert.Equal(RefreshTokenConsumeStatus.Revoked, result.Status);
        Assert.False(validate.IsValid);
        Assert.NotNull(await ScalarAsync(world, world.TenantA, "SELECT revoked_at FROM refresh_tokens WHERE token_hash = @h", ("h", HashOf(late))));
    }

    // Breaks when a logout revokes only the presented token: its whole family must end, and another session of the same user stay.
    [Fact]
    public async Task RevokingATokenRevokesItsWholeFamilyAndNoOtherFamily()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = Guid.NewGuid();
        var (first, family) = await StartFamilyAsync(world, world.TenantA, user);
        var sibling = NewToken();
        await SaveIntoAsync(world, world.TenantA, user, sibling, family);
        var (otherSession, _) = await StartFamilyAsync(world, world.TenantA, user);

        await WithStoreAsync(world, world.TenantA, s => s.RevokeAsync(first, TestContext.Current.CancellationToken));

        Assert.Equal(RefreshTokenConsumeStatus.Revoked, (await ConsumeAsync(world, world.TenantA, sibling)).Status);
        Assert.Equal(RefreshTokenConsumeStatus.Revoked, (await ConsumeAsync(world, world.TenantA, first)).Status);
        Assert.Equal(RefreshTokenConsumeStatus.Consumed, (await ConsumeAsync(world, world.TenantA, otherSession)).Status);
    }

    // Breaks when revoking an unknown or empty token throws or touches rows (a logout with a stale cookie must be harmless).
    [Fact]
    public async Task RevokingAnUnknownOrEmptyTokenChangesNothingAndDoesNotThrow()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var (token, _) = await StartFamilyAsync(world, world.TenantA, Guid.NewGuid());

        await WithStoreAsync(world, world.TenantA, async s =>
        {
            await s.RevokeAsync("never-saved-token", TestContext.Current.CancellationToken);
            await s.RevokeAsync(string.Empty, TestContext.Current.CancellationToken);
        });

        Assert.Equal(RefreshTokenConsumeStatus.Consumed, (await ConsumeAsync(world, world.TenantA, token)).Status);
    }

    // Breaks when a token revoked on its own is consumable.
    [Fact]
    public async Task ATokenRevokedOnItsOwnIsRevoked()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var (token, _) = await StartFamilyAsync(world, world.TenantA, Guid.NewGuid());
        await ExecuteAsync(world, world.TenantA, "UPDATE refresh_tokens SET revoked_at = now()");

        Assert.Equal(RefreshTokenConsumeStatus.Revoked, (await ConsumeAsync(world, world.TenantA, token)).Status);
    }

    // Breaks when the classification does not follow the contract's order: a consumed token of a revoked family is Revoked
    // (family first); a consumed token that has also expired is Reused and revokes its family.
    [Fact]
    public async Task TheFailureIsClassifiedInTheOrderOfTheContract()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = Guid.NewGuid();
        var (revokedFamilyToken, family) = await StartFamilyAsync(world, world.TenantA, user);
        await ConsumeAsync(world, world.TenantA, revokedFamilyToken);
        await ExecuteAsync(world, world.TenantA, "UPDATE refresh_token_families SET revoked_at = now() WHERE id = @f", ("f", Guid.Parse(family)));
        var (expiredConsumed, otherFamily) = await StartFamilyAsync(world, world.TenantA, user);
        await ConsumeAsync(world, world.TenantA, expiredConsumed);
        await ExecuteAsync(world, world.TenantA, "UPDATE refresh_tokens SET expires_at = now() - interval '1 minute' WHERE token_hash = @h", ("h", HashOf(expiredConsumed)));

        var familyRevoked = await ConsumeAsync(world, world.TenantA, revokedFamilyToken);
        var reused = await ConsumeAsync(world, world.TenantA, expiredConsumed);

        Assert.Equal(RefreshTokenConsumeStatus.Revoked, familyRevoked.Status);
        Assert.Equal(RefreshTokenConsumeStatus.Reused, reused.Status);
        Assert.NotNull(await ScalarAsync(world, world.TenantA, "SELECT revoked_at FROM refresh_token_families WHERE id = @f", ("f", Guid.Parse(otherFamily))));
    }

    // Breaks when the user's other devices survive a "sign out everywhere", or when it reaches another user or another tenant
    // (the same user id in tenant B is another account there).
    [Fact]
    public async Task RevokingAllForAUserRevokesEveryFamilyOfThatUserInThisTenantOnly()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = Guid.NewGuid();
        var (device1, _) = await StartFamilyAsync(world, world.TenantA, user);
        var (device2, _) = await StartFamilyAsync(world, world.TenantA, user);
        var (neighbour, _) = await StartFamilyAsync(world, world.TenantA, Guid.NewGuid());
        var (inTenantB, _) = await StartFamilyAsync(world, world.TenantB, user);

        await WithStoreAsync(world, world.TenantA, s => s.RevokeAllForUserAsync(Id(user), TestContext.Current.CancellationToken));

        Assert.Equal(RefreshTokenConsumeStatus.Revoked, (await ConsumeAsync(world, world.TenantA, device1)).Status);
        Assert.Equal(RefreshTokenConsumeStatus.Revoked, (await ConsumeAsync(world, world.TenantA, device2)).Status);
        Assert.Equal(RefreshTokenConsumeStatus.Consumed, (await ConsumeAsync(world, world.TenantA, neighbour)).Status);
        Assert.Equal(RefreshTokenConsumeStatus.Consumed, (await ConsumeAsync(world, world.TenantB, inTenantB)).Status);
    }

    // Breaks when a user id that is not a GUID throws or revokes something.
    [Fact]
    public async Task RevokingAllForAUserIdThatIsNotAGuidChangesNothing()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var (token, _) = await StartFamilyAsync(world, world.TenantA, Guid.NewGuid());

        await WithStoreAsync(world, world.TenantA, s => s.RevokeAllForUserAsync("not-a-guid", TestContext.Current.CancellationToken));

        Assert.Equal(RefreshTokenConsumeStatus.Consumed, (await ConsumeAsync(world, world.TenantA, token)).Status);
    }

    // ---- Validate ----

    // Breaks when Validate consumes the token, or does not return the owner.
    [Fact]
    public async Task ValidatingAValidTokenReturnsItsOwnerAndDoesNotConsumeIt()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = Guid.NewGuid();
        var (token, _) = await StartFamilyAsync(world, world.TenantA, user);

        var first = await ValidateAsync(world, world.TenantA, token);
        var second = await ValidateAsync(world, world.TenantA, token);
        var consume = await ConsumeAsync(world, world.TenantA, token);

        Assert.True(first.IsValid);
        Assert.Equal(Id(user), first.UserId);
        Assert.Equal(Id(world.TenantA), first.TenantId);
        Assert.True(second.IsValid);
        Assert.Equal(RefreshTokenConsumeStatus.Consumed, consume.Status);
    }

    // Breaks when Validate accepts a consumed, revoked or unknown token, or when it revokes the family on reuse (it is read-only).
    [Fact]
    public async Task ValidatingAConsumedRevokedOrUnknownTokenIsInvalidAndChangesNothing()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = Guid.NewGuid();
        var (consumed, family) = await StartFamilyAsync(world, world.TenantA, user);
        var sibling = NewToken();
        await SaveIntoAsync(world, world.TenantA, user, sibling, family);
        await ConsumeAsync(world, world.TenantA, consumed);
        var (revoked, _) = await StartFamilyAsync(world, world.TenantA, user);
        await WithStoreAsync(world, world.TenantA, s => s.RevokeAsync(revoked, TestContext.Current.CancellationToken));

        var consumedResult = await ValidateAsync(world, world.TenantA, consumed);
        var revokedResult = await ValidateAsync(world, world.TenantA, revoked);
        var unknown = await ValidateAsync(world, world.TenantA, "never-saved-token");
        var empty = await ValidateAsync(world, world.TenantA, string.Empty);

        Assert.False(consumedResult.IsValid);
        Assert.False(revokedResult.IsValid);
        Assert.False(unknown.IsValid);
        Assert.False(empty.IsValid);
        Assert.Equal(RefreshTokenConsumeStatus.Consumed, (await ConsumeAsync(world, world.TenantA, sibling)).Status);
    }
}
