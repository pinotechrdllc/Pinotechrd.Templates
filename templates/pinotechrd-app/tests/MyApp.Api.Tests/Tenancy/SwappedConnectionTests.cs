using System.Data;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Npgsql;

namespace MyApp.Api.Tests.Tenancy;

// The settings of a connection string are checked for every connection object a tenant context uses, not once per guard: a
// context that opened once and is then given another connection with SetDbConnection must not skip the check. Needs Docker.
[Collection(PostgresContainer.Name)]
public class SwappedConnectionTests(PostgresFixture postgres)
{
    private const string SchemaSql = "SELECT current_schema() AS \"Value\"";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> Unsafe() =>
        new()
        {
            { "Multiplexing=true", "Multiplexing" },
            { "No Reset On Close=true", "No Reset On Close" },
            { "Search Path=other_schema", "Search Path" },
            { "Options=-c search_path=other_schema", "search_path" },
        };

    private string ConnectionStringOf(TenantRig rig, string setting)
    {
        var connection = postgres.Connection(rig.Database);
        connection.MaxPoolSize = 8;
        return connection.ConnectionString + (setting.Length == 0 ? string.Empty : ";" + setting);
    }

    // The sync checks are in their own method: they block on purpose.
    private static void SyncRefusals(AccessContext context, string token)
    {
        var query = Assert.Throws<InvalidOperationException>(() => context.Items.ToList());
        Assert.Contains(token, query.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() => context.Database.OpenTenantConnection());
        context.Items.Add(new FixtureItem { Name = "never-sync" });
        Assert.ThrowsAny<Exception>(() => context.SaveChanges());
        context.ChangeTracker.Clear();
    }

    // Breaks when the check of the connection's settings runs once per guard: the context opened and bound once, then gets a
    // connection with an unsafe setting, and every use of it must be refused (async and sync, query, save, helper).
    [Theory]
    [MemberData(nameof(Unsafe))]
    public async Task ASwappedInConnectionWithAnUnsafeSettingIsRefusedOnEveryUse(string setting, string token)
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 3);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
        Assert.Equal(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
        await using var swapped = new NpgsqlConnection(ConnectionStringOf(rig, setting));
        context.Database.SetDbConnection(swapped);

        var query = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Items.ToListAsync(Token));
        Assert.Contains(token, query.Message, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.OpenTenantConnectionAsync(Token));
        context.Items.Add(new FixtureItem { Name = "never" });
        await Assert.ThrowsAnyAsync<Exception>(() => context.SaveChangesAsync(Token));
        context.ChangeTracker.Clear();
        SyncRefusals(context, token);
        Assert.Equal(ConnectionState.Closed, swapped.State);
        Assert.Empty(await rig.ColumnAsync($"SELECT name FROM \"{TenantSchemaName.For(tenant)}\".access_items"));
    }

    // The reviewer's reproduction: many parallel scopes of two tenants each swap in a multiplexing connection after their first
    // use. Before the fix a query returned the other tenant's rows (77 of 1 000). No query may return a row, let alone another
    // tenant's, and every use is refused.
    [Fact]
    public async Task ParallelScopesSwappingInAMultiplexingConnectionNeverReadAnotherTenantsRows()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 6);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        await rig.ProvisionAsync(a);
        await rig.ProvisionAsync(b);
        foreach (var (tenant, tag) in new[] { (a, "row-of-a"), (b, "row-of-b") })
        {
            await rig.ExecuteAsync($"INSERT INTO \"{TenantSchemaName.For(tenant)}\".access_items (id, name) VALUES (gen_random_uuid(), '{tag}')");
        }

        var multiplexing = ConnectionStringOf(rig, "Multiplexing=true");
        var wrong = new ConcurrentBag<string>();
        var refused = 0;
        const int Iterations = 400;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, Iterations),
            new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = Token },
            async (i, ct) =>
            {
                var (mine, theirs) = i % 2 == 0 ? (a, "row-of-b") : (b, "row-of-a");
                await using var scope = rig.ScopeFor(mine);
                var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
                await context.Items.ToListAsync(ct);
                await using var swapped = new NpgsqlConnection(multiplexing);
                context.Database.SetDbConnection(swapped);
                try
                {
                    foreach (var name in await context.Items.Select(x => x.Name).ToListAsync(ct))
                    {
                        wrong.Add(name);
                    }
                }
                catch (InvalidOperationException)
                {
                    Interlocked.Increment(ref refused);
                }

                if (wrong.Contains(theirs))
                {
                    throw new InvalidOperationException("Read another tenant's row.");
                }
            });

        Assert.Empty(wrong);
        Assert.Equal(Iterations, refused);
    }

    // Breaks when a swapped-in connection that is safe and closed is refused: it is opened and bound like any other, and reads
    // the tenant's schema.
    [Fact]
    public async Task ASwappedInSafeClosedConnectionIsOpenedAndBoundNormally()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 3);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
        await context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token);
        await using var swapped = new NpgsqlConnection(ConnectionStringOf(rig, string.Empty));
        context.Database.SetDbConnection(swapped);

        Assert.Equal(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
    }

    // Breaks when a swapped-in connection that arrives already open (so the guard did not bind it) is used.
    [Fact]
    public async Task ASwappedInSafeConnectionThatIsAlreadyOpenIsRefused()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 3);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
        await context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token);
        await using var swapped = new NpgsqlConnection(ConnectionStringOf(rig, string.Empty));
        await swapped.OpenAsync(Token);
        context.Database.SetDbConnection(swapped);

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
    }
}
