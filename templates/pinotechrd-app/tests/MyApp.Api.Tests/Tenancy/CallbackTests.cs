using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Npgsql;

namespace MyApp.Api.Tests.Tenancy;

// The callback of AddTenantDbContext runs first and the building block's guarantees are applied after it: whatever the
// callback does, the context either keeps the guard, the shared data source and the unqualified history table, or its first
// open fails with a clear message. Never a working context without the guard. Needs Docker.
[Collection(PostgresContainer.Name)]
public class CallbackTests(PostgresFixture postgres)
{
    private const string SchemaSql = "SELECT current_schema() AS \"Value\"";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Action<IServiceCollection> Register(Action<IServiceProvider, DbContextOptionsBuilder> callback) =>
        services =>
        {
            services.AddTenantDbContext<AccessContext>("Access", callback);
            services.AddTenantMigrator();
        };

    // Breaks when a callback that sets another history table, with a schema, wins: the history must stay
    // __ef_migrations_access, unqualified, in the tenant's schema, and nothing may be created in that schema. (If the first
    // open fails instead, the message must say why.)
    [Fact]
    public async Task ACallbackSettingAnotherQualifiedHistoryTableCannotMoveIt()
    {
        await using var rig = await TenantRig.StartAsync(
            postgres,
            register: Register((provider, options) => options.UseNpgsql(
                provider.GetRequiredService<NpgsqlDataSource>(), npgsql => npgsql.MigrationsHistoryTable("evil_history", "evil_schema"))));
        var tenant = Guid.NewGuid();

        var result = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token);

        if (result.Succeeded)
        {
            Assert.Equal([AccessItems.Id, AccessCreatedBy.Id], await rig.HistoryAsync(tenant, "Access"));
            Assert.False(await rig.SchemaExistsAsync("evil_schema"));
            Assert.DoesNotContain(await rig.UserTablesAsync(), t => t.Contains("evil_history", StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains("history table", Assert.IsType<InvalidOperationException>(result.Error).Message, StringComparison.Ordinal);
        }
    }

    // Breaks when a callback that points the context at another data source (another database) wins: the context must keep
    // the shared data source. Tenant work happens in the shared database and nothing reaches the other one. (Or the first open
    // fails with a message.)
    [Fact]
    public async Task ACallbackReplacingTheDataSourceCannotMoveTheContextToAnotherDatabase()
    {
        var otherConnection = await postgres.CreateDatabaseAsync();
        await using var other = new NpgsqlDataSourceBuilder(otherConnection).Build();
        await using var drop = new DatabaseDrop(postgres, new NpgsqlConnectionStringBuilder(otherConnection).Database!);
        await using var rig = await TenantRig.StartAsync(postgres, register: Register((_, options) => options.UseNpgsql(other)));
        var tenant = Guid.NewGuid();

        var result = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token);

        await using var otherCommand = other.CreateCommand("SELECT count(*) FROM pg_namespace WHERE nspname LIKE 'tenant\\_%'");
        Assert.Equal(0L, await otherCommand.ExecuteScalarAsync(Token));
        if (result.Succeeded)
        {
            Assert.Equal([AccessItems.Id, AccessCreatedBy.Id], await rig.HistoryAsync(tenant, "Access"));
        }
        else
        {
            Assert.IsType<InvalidOperationException>(result.Error);
        }
    }

    // Breaks when a callback that replaces the whole core extension (dropping the interceptors) leaves a working context with
    // no guard: tenant isolation holds, and a connection EF did not bind is refused.
    [Fact]
    public async Task ACallbackReplacingTheCoreOptionsStillLeavesTheGuard()
    {
        await using var rig = await TenantRig.StartAsync(
            postgres,
            maxPoolSize: 3,
            register: Register((_, options) =>
                ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(new CoreOptionsExtension())));
        var tenant = Guid.NewGuid();
        Assert.True((await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token)).Succeeded);
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();

        Assert.Equal(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
        await using var external = await rig.DataSource.OpenConnectionAsync(Token);
        await context.Database.CloseConnectionAsync();
        context.Database.SetDbConnection(external);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
    }

    // Breaks when a callback that adds its own interceptor and options (detailed errors, sensitive logging) is no longer
    // applied, or lets the context lose its isolation.
    [Fact]
    public async Task ACallbackAddingInterceptorsAndOptionsIsKeptAndTheContextStillIsolates()
    {
        var opened = 0;
        await using var rig = await TenantRig.StartAsync(
            postgres,
            maxPoolSize: 3,
            register: Register((_, options) =>
            {
                options.EnableDetailedErrors();
                options.AddInterceptors(new CountingInterceptor(() => Interlocked.Increment(ref opened)));
            }));
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        await rig.ProvisionAsync(a);
        await rig.ProvisionAsync(b);
        var openedBefore = Volatile.Read(ref opened);

        foreach (var tenant in new[] { a, b, a })
        {
            await using var scope = rig.ScopeFor(tenant);
            var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
            Assert.Equal(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
        }

        Assert.True(Volatile.Read(ref opened) > openedBefore, "The callback's own interceptor was not applied.");
    }

    private sealed class CountingInterceptor(Action onOpened) : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
    {
        public override Task ConnectionOpenedAsync(
            System.Data.Common.DbConnection connection,
            Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            onOpened();
            return Task.CompletedTask;
        }

        public override void ConnectionOpened(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData) =>
            onOpened();
    }
}
