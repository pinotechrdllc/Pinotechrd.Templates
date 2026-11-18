using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Npgsql;

namespace MyApp.Api.Tests.Tenancy;

// Raw access to the connection of a tenant context: opening it outside EF is refused, a connection EF did not bind is not
// used, and OpenTenantConnection is the supported way to run raw SQL. Needs Docker.
[Collection(PostgresContainer.Name)]
public class RawConnectionTests(PostgresFixture postgres)
{
    private const string SchemaSql = "SELECT current_schema() AS \"Value\"";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string SchemaOf(AccessContext context) => context.Database.SqlQueryRaw<string>(SchemaSql).ToList().Single();

    // Breaks when the context's connection can be opened outside EF (it would be open on the default schema), when the
    // refused open leaves the connection open, or when EF cannot read the tenant's schema afterwards.
    [Fact]
    public async Task OpeningTheContextsConnectionOutsideEfThrowsLeavesItClosedAndEfStillReadsTheTenantSchema()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
        var connection = context.Database.GetDbConnection();

        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.OpenAsync(Token));
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Equal(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));

        await context.Database.CloseConnectionAsync();
        SyncOpenRefused(connection);
        Assert.Equal(TenantSchemaName.For(tenant), SchemaOf(context));
    }

    private static void SyncOpenRefused(System.Data.Common.DbConnection connection)
    {
        Assert.Throws<InvalidOperationException>(() => connection.Open());
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    // Breaks when a connection opened by someone else and given to the context with SetDbConnection is used by EF (it is on
    // the default schema): its commands must throw. Async and sync.
    [Fact]
    public async Task AnExternallyOpenedConnectionGivenToTheContextMakesEfCommandsThrow()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 3);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
        await using var external = await rig.DataSource.OpenConnectionAsync(Token);
        context.Database.SetDbConnection(external);

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Items.ToListAsync(Token));
        context.Items.Add(new FixtureItem { Name = "never" });
        var saveFailure = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(Token));
        Assert.IsType<InvalidOperationException>(saveFailure.InnerException);
        context.ChangeTracker.Clear();
        Assert.Throws<InvalidOperationException>(() => SchemaOf(context));
        Assert.Empty(await rig.ColumnAsync($"SELECT name FROM \"{TenantSchemaName.For(tenant)}\".access_items"));
    }

    // Breaks when OpenTenantConnectionAsync returns a connection that is not on the tenant's schema, cannot run a raw
    // command (the Dapper pattern), or when the context's own EF queries on it leave the tenant.
    [Fact]
    public async Task OpenTenantConnectionAsyncGivesAConnectionOnTheTenantSchemaForRawSqlAndEf()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        await rig.ProvisionAsync(a);
        await rig.ProvisionAsync(b);
        await using var scope = rig.ScopeFor(a);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();

        var connection = await context.Database.OpenTenantConnectionAsync(Token);

        Assert.Equal(ConnectionState.Open, connection.State);
        await using (var raw = new NpgsqlCommand("SELECT current_schema()", connection))
        {
            Assert.Equal(TenantSchemaName.For(a), await raw.ExecuteScalarAsync(Token));
        }

        await using (var insert = new NpgsqlCommand($"INSERT INTO access_items (id, name) VALUES ('{Guid.NewGuid()}', 'raw-row')", connection))
        {
            await insert.ExecuteNonQueryAsync(Token);
        }

        Assert.Equal(["raw-row"], await context.Items.Select(i => i.Name).ToListAsync(Token));
        Assert.Equal(TenantSchemaName.For(a), await context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
        Assert.Equal(["raw-row"], await rig.ColumnAsync($"SELECT name FROM \"{TenantSchemaName.For(a)}\".access_items"));
        Assert.Empty(await rig.ColumnAsync($"SELECT name FROM \"{TenantSchemaName.For(b)}\".access_items"));
        await context.Database.CloseConnectionAsync();
    }

    // The same through the synchronous helper.
    [Fact]
    public async Task OpenTenantConnectionGivesAConnectionOnTheTenantSchemaForRawSqlAndEf()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();

        SyncRaw(context, tenant);
    }

    private static void SyncRaw(AccessContext context, Guid tenant)
    {
        var connection = context.Database.OpenTenantConnection();
        using (var raw = new NpgsqlCommand("SELECT current_schema()", connection))
        {
            Assert.Equal(TenantSchemaName.For(tenant), raw.ExecuteScalar());
        }

        Assert.Equal(TenantSchemaName.For(tenant), SchemaOf(context));
        context.Database.CloseConnection();
    }

    // Breaks when the helper works on a context that is not a tenant context (it would hand out an unbound connection).
    [Fact]
    public async Task OpenTenantConnectionOnANonTenantContextThrows()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var options = new DbContextOptionsBuilder<PlainContext>();
        options.UseNpgsql(rig.DataSource);
        await using var context = new PlainContext(options.Options);

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.OpenTenantConnectionAsync(Token));
        Assert.Throws<InvalidOperationException>(() => context.Database.OpenTenantConnection());
    }

    // Breaks when a context of tenant B can run on the open connection of tenant A (a connection handed over, or leaked from
    // a shared helper): reading, or writing, would touch A's schema.
    [Fact]
    public async Task AContextOfAnotherTenantAdoptingAnOpenConnectionIsRefused()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 3);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        await rig.ProvisionAsync(a);
        await rig.ProvisionAsync(b);
        await using var scopeA = rig.ScopeFor(a);
        await using var scopeB = rig.ScopeFor(b);
        var contextA = scopeA.ServiceProvider.GetRequiredService<AccessContext>();
        var contextB = scopeB.ServiceProvider.GetRequiredService<AccessContext>();
        var connectionOfA = await contextA.Database.OpenTenantConnectionAsync(Token);
        contextB.Database.SetDbConnection(connectionOfA);

        await Assert.ThrowsAsync<InvalidOperationException>(() => contextB.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
        contextB.Items.Add(new FixtureItem { Name = "leak-to-a" });
        var saveFailure = await Assert.ThrowsAsync<DbUpdateException>(() => contextB.SaveChangesAsync(Token));
        Assert.IsType<InvalidOperationException>(saveFailure.InnerException);
        Assert.Equal(TenantSchemaName.For(a), await contextA.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
        Assert.Empty(await rig.ColumnAsync($"SELECT name FROM \"{TenantSchemaName.For(a)}\".access_items"));
        await contextA.Database.CloseConnectionAsync();
    }

    // Breaks when two contexts of the same tenant cannot share one connection and one transaction (the supported way to
    // write through two modules atomically), or when the two writes do not commit and roll back together.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TwoContextsOfTheSameTenantSharingAConnectionAndATransactionCommitOrRollBackTogether(bool commit)
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        var schema = TenantSchemaName.For(tenant);

        await using (var scope = rig.ScopeFor(tenant))
        {
            var access = scope.ServiceProvider.GetRequiredService<AccessContext>();
            var billing = scope.ServiceProvider.GetRequiredService<BillingContext>();
            var connection = await access.Database.OpenTenantConnectionAsync(Token);
            billing.Database.SetDbConnection(connection);
            await using var transaction = await access.Database.BeginTransactionAsync(Token);
            await billing.Database.UseTransactionAsync(transaction.GetDbTransaction(), Token);
            access.Items.Add(new FixtureItem { Name = "shared-access" });
            billing.Items.Add(new FixtureItem { Name = "shared-billing" });
            await access.SaveChangesAsync(Token);
            await billing.SaveChangesAsync(Token);
            if (commit)
            {
                await transaction.CommitAsync(Token);
            }
            else
            {
                await transaction.RollbackAsync(Token);
            }

            await access.Database.CloseConnectionAsync();
        }

        Assert.Equal(commit ? ["shared-access"] : [], await rig.ColumnAsync($"SELECT name FROM \"{schema}\".access_items"));
        Assert.Equal(commit ? ["shared-billing"] : [], await rig.ColumnAsync($"SELECT name FROM \"{schema}\".billing_items"));
    }

    // Breaks when the physical connection a scope returned to the pool keeps its tenant: with a pool of one the next scope,
    // of another tenant, gets the very same backend (the pids match) and must be bound to ITS tenant, never to the first.
    [Fact]
    public async Task ThePhysicalConnectionReusedByTheNextScopeOfAnotherTenantIsBoundToThatTenant()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 1);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        await rig.ProvisionAsync(a);
        await rig.ProvisionAsync(b);
        long pidOfA;
        await using (var scopeA = rig.ScopeFor(a))
        {
            var context = scopeA.ServiceProvider.GetRequiredService<AccessContext>();
            context.Items.Add(new FixtureItem { Name = "only-a" });
            await context.SaveChangesAsync(Token);
            pidOfA = await context.Database.SqlQueryRaw<long>("SELECT pg_backend_pid()::bigint AS \"Value\"").SingleAsync(Token);
        }

        await using var scopeB = rig.ScopeFor(b);
        var contextB = scopeB.ServiceProvider.GetRequiredService<AccessContext>();
        var pidOfB = await contextB.Database.SqlQueryRaw<long>("SELECT pg_backend_pid()::bigint AS \"Value\"").SingleAsync(Token);

        Assert.Equal(pidOfA, pidOfB);
        Assert.Equal(TenantSchemaName.For(b), await contextB.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
        Assert.Empty(await contextB.Items.ToListAsync(Token));
    }
}
