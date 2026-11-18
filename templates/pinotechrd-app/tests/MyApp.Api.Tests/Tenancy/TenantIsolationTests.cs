using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Npgsql;

namespace MyApp.Api.Tests.Tenancy;

// A context registered with AddTenantDbContext reads and writes only the current tenant's schema, whichever tenant used
// the pooled connection before. Needs Docker. The pools are small on purpose, so connections are reused all the time.
[Collection(PostgresContainer.Name)]
public class TenantIsolationTests(PostgresFixture postgres)
{
    private const string CurrentSchemaSql = "SELECT current_schema() AS \"Value\"";

    private static string Tag(Guid tenant) => tenant.ToString("N")[..8];

    // One unit of work of a tenant in a scope of its own: the schema of the connection, a write, and a read that must
    // show this tenant's rows only.
    private static async Task WorkAsync(TenantRig rig, Guid tenant, string name)
    {
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();

        var schema = await context.Database.SqlQueryRaw<string>(CurrentSchemaSql).SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TenantSchemaName.For(tenant), schema);

        context.Items.Add(new FixtureItem { Name = name });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var names = await context.Items.Select(i => i.Name).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(name, names);
        Assert.All(names, n => Assert.StartsWith(Tag(tenant) + "-", n, StringComparison.Ordinal));
        Assert.Equal(schema, await context.Database.SqlQueryRaw<string>(CurrentSchemaSql).SingleAsync(TestContext.Current.CancellationToken));
    }

    // The same through the synchronous open path of EF Core. Not async on purpose: it is the path under test.
    private static void WorkSync(TenantRig rig, Guid tenant, string name)
    {
        using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();

        Assert.Equal(TenantSchemaName.For(tenant), context.Database.SqlQueryRaw<string>(CurrentSchemaSql).ToList().Single());

        context.Items.Add(new FixtureItem { Name = name });
        context.SaveChanges();

        var names = context.Items.Select(i => i.Name).ToList();
        Assert.Contains(name, names);
        Assert.All(names, n => Assert.StartsWith(Tag(tenant) + "-", n, StringComparison.Ordinal));

        var connection = context.Database.GetDbConnection();
        context.Database.OpenConnection();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT current_schema()";
            Assert.Equal(TenantSchemaName.For(tenant), command.ExecuteScalar());
        }
        finally
        {
            context.Database.CloseConnection();
        }
    }

    private static async Task AssertStoredRowsAsync(TenantRig rig, Guid tenant, IEnumerable<string> expected)
    {
        var stored = await rig.ColumnAsync($"SELECT name FROM \"{TenantSchemaName.For(tenant)}\".access_items ORDER BY name");
        Assert.Equal(expected.Order(StringComparer.Ordinal), stored);
    }

    // Breaks when the search_path is not set on a connection reused from the pool, when it is set to the wrong tenant, or
    // when a row lands in or is read from another tenant's schema: 20 alternations on a pool of two connections.
    [Fact]
    public async Task TwentyAlternationsBetweenTwoTenantsOnASmallPoolStayInTheirOwnSchemas()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        await rig.ProvisionAsync(a);
        await rig.ProvisionAsync(b);

        List<string> forA = [];
        List<string> forB = [];
        for (var i = 0; i < 20; i++)
        {
            var tenant = i % 2 == 0 ? a : b;
            var name = $"{Tag(tenant)}-{i}";
            await WorkAsync(rig, tenant, name);
            (tenant == a ? forA : forB).Add(name);
        }

        await AssertStoredRowsAsync(rig, a, forA);
        await AssertStoredRowsAsync(rig, b, forB);
        Assert.DoesNotContain(await rig.UserTablesAsync(), t => t.StartsWith("public.", StringComparison.Ordinal));
    }

    // Breaks when concurrent scopes of different tenants share state (a tenant that follows the thread or the connection)
    // on a pool of three connections that eight workers compete for.
    [Fact]
    public async Task ParallelWorkOfTwoTenantsOnAPoolOfThreeStaysInTheirOwnSchemas()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 3);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        await rig.ProvisionAsync(a);
        await rig.ProvisionAsync(b);
        var forA = new System.Collections.Concurrent.ConcurrentBag<string>();
        var forB = new System.Collections.Concurrent.ConcurrentBag<string>();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, 40),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = TestContext.Current.CancellationToken },
            async (i, _) =>
            {
                var tenant = i % 2 == 0 ? a : b;
                var name = $"{Tag(tenant)}-{i}";
                await WorkAsync(rig, tenant, name);
                (tenant == a ? forA : forB).Add(name);
            });

        Assert.Equal(20, forA.Count);
        Assert.Equal(20, forB.Count);
        await AssertStoredRowsAsync(rig, a, forA);
        await AssertStoredRowsAsync(rig, b, forB);
    }

    // Breaks when the synchronous open (OpenConnection, SaveChanges, a sync query) skips the interceptor.
    [Fact]
    public async Task SynchronousOpenPathStaysInTheCurrentTenantsSchema()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        await rig.ProvisionAsync(a);
        await rig.ProvisionAsync(b);

        List<string> forA = [];
        List<string> forB = [];
        for (var i = 0; i < 20; i++)
        {
            var tenant = i % 2 == 0 ? a : b;
            var name = $"{Tag(tenant)}-{i}";
            WorkSync(rig, tenant, name);
            (tenant == a ? forA : forB).Add(name);
        }

        await AssertStoredRowsAsync(rig, a, forA);
        await AssertStoredRowsAsync(rig, b, forB);
    }

    // Breaks when a connection that served a tenant goes back to the pool with that tenant's search_path: with one
    // physical connection in the pool, the raw connection taken afterwards is the very one the tenant used.
    [Fact]
    public async Task ARawPooledConnectionIsBackToTheDefaultSearchPathAfterTenantUse()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 1);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        long tenantPid;
        await using (var scope = rig.ScopeFor(tenant))
        {
            var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
            tenantPid = await context.Database.SqlQueryRaw<long>("SELECT pg_backend_pid()::bigint AS \"Value\"").SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>(CurrentSchemaSql).SingleAsync(TestContext.Current.CancellationToken));
        }

        await using var raw = await rig.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT pg_backend_pid()::bigint, current_schema(), current_setting('search_path')", raw);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));

        Assert.Equal(tenantPid, reader.GetInt64(0));
        Assert.Equal("public", reader.GetString(1));
        Assert.Equal(await rig.ScalarAsync<string>("SELECT current_setting('search_path')"), reader.GetString(2));
        Assert.DoesNotContain("tenant_", reader.GetString(2), StringComparison.Ordinal);
    }

    // Breaks when a tenant's rows are visible without the tenant: the raw read of the shared data source finds no table.
    [Fact]
    public async Task ARawConnectionSeesNoTenantTable()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        await using (var scope = rig.ScopeFor(tenant))
        {
            scope.ServiceProvider.GetRequiredService<AccessContext>().Items.Add(new FixtureItem { Name = "x" });
            await scope.ServiceProvider.GetRequiredService<AccessContext>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var command = rig.DataSource.CreateCommand("SELECT count(*) FROM access_items");

        var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Equal("42P01", failure.SqlState);
    }
}
