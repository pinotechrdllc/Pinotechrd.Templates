using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Application.Tenancy;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Npgsql;

namespace MyApp.Api.Tests.Tenancy;

// When the step that binds a connection to the tenant fails after the connection opened, the operation fails, the
// connection is closed and the next use of the context is bound to the tenant: never left open on the default schema.
// The failure is made deterministic by an interceptor placed before the guard (a context built outside dependency
// injection lets the test choose the order): it acts right after the open, so the bind statement meets the failure.
// Needs Docker.
[Collection(PostgresContainer.Name)]
public class FailedBindTests(PostgresFixture postgres)
{
    private const string SchemaSql = "SELECT current_schema() AS \"Value\"";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Acts once, right after the physical open and before the guard binds the connection.
    private sealed class BindSaboteur : DbConnectionInterceptor
    {
        private int _armed;

        public Action<NpgsqlConnection>? Sabotage { get; set; }

        public void Arm() => Volatile.Write(ref _armed, 1);

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Sabotage?.Invoke((NpgsqlConnection)connection);
            }
        }

        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            ConnectionOpened(connection, eventData);
            return Task.CompletedTask;
        }
    }

    private static AccessContext ContextOf(TenantRig rig, ICurrentTenant tenant, BindSaboteur saboteur)
    {
        var options = new DbContextOptionsBuilder<AccessContext>();
        options.AddInterceptors(saboteur);
        options.UseTenantPersistence(rig.DataSource, tenant, "Access", allowPrivilegedLogin: true);
        return new AccessContext(options.Options);
    }

    private static Action<NpgsqlConnection> TerminateBackend(TenantRig rig) =>
        connection =>
        {
            using var admin = new NpgsqlConnection(rig.AdminConnectionString);
            admin.Open();
            using var command = new NpgsqlCommand($"SELECT pg_terminate_backend({connection.ProcessID})", admin);
            Assert.True((bool)command.ExecuteScalar()!);
        };

    private static string SchemaOf(AccessContext context) => context.Database.SqlQueryRaw<string>(SchemaSql).ToList().Single();

    // Breaks when a failing server during the bind leaves the connection open (on the default schema), when the operation
    // does not fail, or when the next query in the scope reads another schema than the tenant's. The backend is
    // terminated between the open and the bind statement.
    [Fact]
    public async Task AServerErrorDuringTheBindFailsTheOperationClosesTheConnectionAndTheNextQueryIsBound()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        await using var scope = rig.ScopeFor(tenant);
        var saboteur = new BindSaboteur { Sabotage = TerminateBackend(rig) };
        await using var context = ContextOf(rig, scope.ServiceProvider.GetRequiredService<ICurrentTenant>(), saboteur);

        saboteur.Arm();
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));

        Assert.IsNotType<OperationCanceledException>(failure);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        Assert.Equal(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
    }

    // The same through the synchronous open path.
    [Fact]
    public async Task AServerErrorDuringTheSynchronousBindFailsTheOperationClosesTheConnectionAndTheNextQueryIsBound()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        await using var scope = rig.ScopeFor(tenant);
        var saboteur = new BindSaboteur { Sabotage = TerminateBackend(rig) };
        await using var context = ContextOf(rig, scope.ServiceProvider.GetRequiredService<ICurrentTenant>(), saboteur);

        SyncFailureThenQuery(context, saboteur, tenant);
    }

    private static void SyncFailureThenQuery(AccessContext context, BindSaboteur saboteur, Guid tenant)
    {
        saboteur.Arm();
        Assert.ThrowsAny<Exception>(() => SchemaOf(context));
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        Assert.Equal(TenantSchemaName.For(tenant), SchemaOf(context));
    }

    // Breaks when a cancellation that arrives after the open (the bind statement is cancelled) leaves the connection open on
    // the default schema, or does not surface as a cancellation.
    [Fact]
    public async Task ACancellationDuringTheBindFailsTheOperationClosesTheConnectionAndTheNextQueryIsBound()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        await using var scope = rig.ScopeFor(tenant);
        using var cancel = new CancellationTokenSource();
        var saboteur = new BindSaboteur { Sabotage = _ => cancel.Cancel() };
        await using var context = ContextOf(rig, scope.ServiceProvider.GetRequiredService<ICurrentTenant>(), saboteur);

        saboteur.Arm();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(cancel.Token));

        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        Assert.Equal(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
    }

    // Breaks when a tenant whose schema does not exist (not provisioned) gets a usable connection on the default schema:
    // the open must fail with InvalidOperationException, the connection must be closed, and once the schema exists the same
    // context works. Async path.
    [Fact]
    public async Task ATenantWithNoSchemaFailsAtOpenWithInvalidOperationAndTheConnectionIsClosed()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var tenant = Guid.NewGuid();
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Items.ToListAsync(Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.OpenConnectionAsync(Token));
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);

        await rig.CreateTenantSchemaAsync(tenant);
        Assert.Equal(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
    }

    // The same through the synchronous path.
    [Fact]
    public async Task ATenantWithNoSchemaFailsAtSynchronousOpenWithInvalidOperationAndTheConnectionIsClosed()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var tenant = Guid.NewGuid();
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();

        SyncMissingSchema(context);

        await rig.CreateTenantSchemaAsync(tenant);
        Assert.Equal(TenantSchemaName.For(tenant), SchemaOf(context));
    }

    private static void SyncMissingSchema(AccessContext context)
    {
        Assert.Throws<InvalidOperationException>(() => SchemaOf(context));
        Assert.Throws<InvalidOperationException>(() => context.Items.ToList());
        Assert.Throws<InvalidOperationException>(() => context.Database.OpenConnection());
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }
}
