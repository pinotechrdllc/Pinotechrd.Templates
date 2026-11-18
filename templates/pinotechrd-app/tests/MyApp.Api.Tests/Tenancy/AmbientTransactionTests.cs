using System.Data;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Npgsql;

namespace MyApp.Api.Tests.Tenancy;

// A bind performed inside a transaction that rolls back is undone by PostgreSQL: the connection then runs on the session's
// default path while the guard believes it is bound. The default connection string keeps Enlist on, so a TransactionScope
// around tenant code is the way in. Needs Docker.
[Collection(PostgresContainer.Name)]
public class AmbientTransactionTests(PostgresFixture postgres)
{
    private const string SchemaSql = "SELECT current_schema() AS \"Value\"";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<(TenantRig Rig, Guid Tenant)> TenantWithRowAsync(PostgresFixture postgres)
    {
        var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 3);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        await rig.ExecuteAsync($"INSERT INTO \"{TenantSchemaName.For(tenant)}\".access_items (id, name) VALUES (gen_random_uuid(), 'mine')");
        return (rig, tenant);
    }

    private static async Task AssertNextQueryIsTheTenantsAsync(AccessContext context, Guid tenant)
    {
        Assert.Equal(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
        Assert.Equal(["mine"], await context.Items.Select(i => i.Name).ToListAsync(Token));
    }

    // The synchronous scope is a separate method: it blocks on purpose and must not run in an async method.
    private static void RefusedUnderASynchronousScope(AccessContext context)
    {
        using (new TransactionScope())
        {
            Assert.Throws<InvalidOperationException>(() => context.Items.ToList());
            Assert.Throws<InvalidOperationException>(() => context.Database.SqlQueryRaw<string>(SchemaSql).ToList());
            Assert.Throws<InvalidOperationException>(() => context.Database.OpenTenantConnection());
            Assert.Throws<InvalidOperationException>(() => context.Database.OpenConnection());
            Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        }
    }

    // Breaks when a tenant context opens its connection inside a TransactionScope (the connection enlists, the bind runs
    // inside the scope's transaction and its rollback undoes it): EF queries and both helpers must throw, and once the scope is
    // gone the same context reads the tenant's rows, never public.
    [Fact]
    public async Task ATenantOpenInsideATransactionScopeThrowsAndTheNextQueryOutsideItReadsTheTenant()
    {
        var (rig, tenant) = await TenantWithRowAsync(postgres);
        await using var _ = rig;
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();

        RefusedUnderASynchronousScope(context);

        await AssertNextQueryIsTheTenantsAsync(context, tenant);
    }

    // Breaks when the helper hands out an already open, bound connection inside an ambient transaction (the refusal of the
    // helper itself, separate from the refusal when EF opens the connection): it must throw, and the connection must stay
    // usable outside the scope.
    [Fact]
    public async Task TheHelperRefusesInsideATransactionScopeEvenWhenTheConnectionIsAlreadyOpen()
    {
        var (rig, tenant) = await TenantWithRowAsync(postgres);
        await using var _ = rig;
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
        await context.Database.OpenTenantConnectionAsync(Token);

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.OpenTenantConnectionAsync(Token));
        }

        await AssertNextQueryIsTheTenantsAsync(context, tenant);
    }

    // The same with the scope flowing across awaits.
    [Fact]
    public async Task ATenantOpenInsideAnAsyncFlowTransactionScopeThrowsAcrossAwaitsAndTheNextQueryReadsTheTenant()
    {
        var (rig, tenant) = await TenantWithRowAsync(postgres);
        await using var _ = rig;
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.Items.ToListAsync(Token));
            await Task.Yield();
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.OpenTenantConnectionAsync(Token));
            await Task.Delay(10, Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
            Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        }

        await AssertNextQueryIsTheTenantsAsync(context, tenant);
    }

    // Breaks when the context's own transaction (begun on a closed connection, so EF opens and binds first) lets a rollback
    // undo the binding.
    [Fact]
    public async Task AnEfTransactionBegunOnAClosedConnectionThenRolledBackLeavesTheNextQueryOnTheTenant()
    {
        var (rig, tenant) = await TenantWithRowAsync(postgres);
        await using var _ = rig;
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();

        await using (var transaction = await context.Database.BeginTransactionAsync(Token))
        {
            Assert.Equal(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
            context.Items.Add(new FixtureItem { Name = "rolled-back" });
            await context.SaveChangesAsync(Token);
            await transaction.RollbackAsync(Token);
        }

        context.ChangeTracker.Clear();
        await AssertNextQueryIsTheTenantsAsync(context, tenant);
    }

    // Breaks when a rollback to a savepoint undoes the binding.
    [Fact]
    public async Task ARollbackToASavepointLeavesTheNextQueryOnTheTenant()
    {
        var (rig, tenant) = await TenantWithRowAsync(postgres);
        await using var _ = rig;
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();

        await using (var transaction = await context.Database.BeginTransactionAsync(Token))
        {
            await transaction.CreateSavepointAsync("sp", Token);
            context.Items.Add(new FixtureItem { Name = "undone" });
            await context.SaveChangesAsync(Token);
            await transaction.RollbackToSavepointAsync("sp", Token);
            Assert.Equal(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
            await transaction.RollbackAsync(Token);
        }

        context.ChangeTracker.Clear();
        await AssertNextQueryIsTheTenantsAsync(context, tenant);
    }

    // Breaks when a transaction enlisted in an already bound connection (EnlistTransaction after open) and rolled back
    // changes what the connection sees.
    [Fact]
    public async Task EnlistingATransactionAfterTheOpenThenRollingBackLeavesTheNextQueryOnTheTenant()
    {
        var (rig, tenant) = await TenantWithRowAsync(postgres);
        await using var _ = rig;
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
        var connection = await context.Database.OpenTenantConnectionAsync(Token);

        Enlist(connection, () => context.Database.SqlQueryRaw<string>(SchemaSql).ToList().Single(), TenantSchemaName.For(tenant));

        await AssertNextQueryIsTheTenantsAsync(context, tenant);
    }

    private static void Enlist(NpgsqlConnection connection, Func<string> schemaQuery, string expected)
    {
        using var transaction = new CommittableTransaction();
        connection.EnlistTransaction(transaction);
        Assert.Equal(expected, schemaQuery());
        transaction.Rollback();
    }

    // Breaks when a TransactionScope entered AFTER the connection was opened and bound, then rolled back, leaves the
    // connection on another path: the open connection is not enlisted, so the scope's rollback must not reach the binding.
    [Fact]
    public async Task ATransactionScopeEnteredAfterTheBindThenRolledBackLeavesTheNextQueryOnTheTenant()
    {
        var (rig, tenant) = await TenantWithRowAsync(postgres);
        await using var _ = rig;
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
        await context.Database.OpenTenantConnectionAsync(Token);

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            Assert.Equal(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
        }

        await AssertNextQueryIsTheTenantsAsync(context, tenant);
    }

    // The same sequences with a role-level default search_path that points at tenant B (a login role and a database of this
    // test alone): a tenant-A context never returns B's rows in any of them. Every open fails: inside a TransactionScope because it is
    // refused, otherwise because the session's default is not PostgreSQL's (the message names the pg_db_role_setting entry).
    [Fact]
    public async Task WithARoleDefaultPathTenantAContextNeverReturnsTenantBRowsInAnyAmbientSequence()
    {
        await using var login = await DedicatedLogin.CreateAsync(postgres);
        await using var admin = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        login.Drops(admin.Database);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        await admin.ProvisionAsync(a);
        await admin.ProvisionAsync(b);
        foreach (var tenant in new[] { a, b })
        {
            var schema = TenantSchemaName.For(tenant);
            await admin.ExecuteAsync(
                $"GRANT USAGE ON SCHEMA \"{schema}\" TO {login.Name}; GRANT ALL ON ALL TABLES IN SCHEMA \"{schema}\" TO {login.Name}");
        }

        await admin.ExecuteAsync($"INSERT INTO \"{TenantSchemaName.For(b)}\".access_items (id, name) VALUES (gen_random_uuid(), 'secret-of-b')");
        await admin.ExecuteAsync($"ALTER ROLE {login.Name} IN DATABASE \"{admin.Database}\" SET search_path = \"{TenantSchemaName.For(b)}\"");
        await using var rig = login.RigOn(admin.Database, maxPoolSize: 3);

        var read = new List<string>();
        foreach (var sequence in Sequences())
        {
            await using var scope = rig.ScopeFor(a);
            var context = scope.ServiceProvider.GetRequiredService<AccessContext>();

            var failure = await Record.ExceptionAsync(() => sequence(context, read));

            var refusal = Assert.IsType<InvalidOperationException>(failure);
            Assert.True(
                refusal.Message.Contains("pg_db_role_setting", StringComparison.Ordinal) || refusal.Message.Contains("ambient transaction", StringComparison.Ordinal),
                refusal.Message);
            Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        }

        Assert.DoesNotContain("secret-of-b", read);
    }

    private static IEnumerable<Func<AccessContext, List<string>, Task>> Sequences()
    {
        yield return async (context, read) =>
        {
            using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
            await Names(context, read);
        };
        yield return async (context, read) =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(Token);
            await transaction.RollbackAsync(Token);
            await Names(context, read);
        };
        yield return async (context, read) =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(Token);
            await transaction.CreateSavepointAsync("sp", Token);
            await transaction.RollbackToSavepointAsync("sp", Token);
            await Names(context, read);
        };
        yield return async (context, read) =>
        {
            var connection = await context.Database.OpenTenantConnectionAsync(Token);
            Enlist(connection, () => string.Empty, string.Empty);
            await Names(context, read);
        };
        yield return async (context, read) =>
        {
            await context.Database.OpenConnectionAsync(Token);
            using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
            {
                await Names(context, read);
            }
        };
    }

    private static async Task Names(AccessContext context, List<string> read) =>
        read.AddRange(await context.Items.Select(i => i.Name).ToListAsync(Token));
}
