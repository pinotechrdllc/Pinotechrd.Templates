using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Application.Tenancy;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Npgsql;

namespace MyApp.Api.Tests.Tenancy;

// A context built outside dependency injection with UseTenantPersistence behaves like a registered one, and the callback of
// AddTenantDbContext cannot undo the tenant configuration. Needs Docker.
[Collection(PostgresContainer.Name)]
public class ManualOptionsTests(PostgresFixture postgres)
{
    private const string SchemaSql = "SELECT current_schema() AS \"Value\"";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static AccessContext Manual(NpgsqlDataSource dataSource, ICurrentTenant tenant)
    {
        var options = new DbContextOptionsBuilder<AccessContext>();
        options.UseTenantPersistence(dataSource, tenant, "Access");
        return new AccessContext(options.Options);
    }

    // Breaks when a context built from UseTenantPersistence is not bound to the current tenant on every open: alternating
    // tenants on a pool of two, each with its own scope's ICurrentTenant.
    [Fact]
    public async Task AManuallyBuiltContextIsIsolatedLikeARegisteredOne()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        await rig.ProvisionAsync(a);
        await rig.ProvisionAsync(b);

        for (var i = 0; i < 6; i++)
        {
            var tenant = i % 2 == 0 ? a : b;
            var tag = tenant.ToString("N")[..8];
            await using var scope = rig.ScopeFor(tenant);
            await using var context = Manual(rig.DataSource, scope.ServiceProvider.GetRequiredService<ICurrentTenant>());
            context.Items.Add(new FixtureItem { Name = $"{tag}-{i}" });
            await context.SaveChangesAsync(Token);

            Assert.Equal(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
            Assert.All(await context.Items.Select(x => x.Name).ToListAsync(Token), n => Assert.StartsWith(tag, n, StringComparison.Ordinal));
        }
    }

    // Breaks when a manually built context opens without a tenant, or reaches the server when it does.
    [Fact]
    public async Task AManuallyBuiltContextRefusesToOpenWithoutATenantAndSendsNothing()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        await using var scope = rig.Services.CreateAsyncScope();
        await using var context = Manual(rig.DataSource, scope.ServiceProvider.GetRequiredService<ICurrentTenant>());
        var before = rig.CommandsSent;

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Items.ToListAsync(Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.MigrateAsync(Token));

        Assert.Equal(before, rig.CommandsSent);
        Assert.Empty(await rig.UserTablesAsync());
    }

    // Breaks when a manually built context does not use the module's history table, unqualified, in the tenant's schema.
    [Fact]
    public async Task AManuallyBuiltContextMigratesIntoTheModulesHistoryTableOfTheTenantSchema()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var tenant = Guid.NewGuid();
        await rig.ExecuteAsync($"CREATE SCHEMA \"{TenantSchemaName.For(tenant)}\"");
        await using var scope = rig.ScopeFor(tenant);
        await using var context = Manual(rig.DataSource, scope.ServiceProvider.GetRequiredService<ICurrentTenant>());

        await context.Database.MigrateAsync(Token);

        Assert.Equal([AccessItems.Id, AccessCreatedBy.Id], await rig.HistoryAsync(tenant, "Access"));
        Assert.Equal(
            [$"{TenantSchemaName.For(tenant)}.__ef_migrations_access", $"{TenantSchemaName.For(tenant)}.access_items"],
            await rig.UserTablesAsync());
    }

    // Breaks when the callback of AddTenantDbContext does not run, or runs before the tenant configuration.
    [Fact]
    public async Task TheAddTenantDbContextCallbackRunsOnEveryContext()
    {
        var calls = 0;
        await using var rig = await TenantRig.StartAsync(
            postgres,
            register: services =>
            {
                services.AddTenantDbContext<AccessContext>("Access", (_, options) =>
                {
                    Interlocked.Increment(ref calls);
                    options.EnableDetailedErrors();
                });
                services.AddTenantMigrator();
            });
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();

        Assert.Equal(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));

        Assert.True(calls >= 1);
    }

    // Breaks when the callback can change the history table (it calls UseNpgsql again with another one): the migrator and the
    // README rely on __ef_migrations_<module>.
    [Fact]
    public async Task TheCallbackCannotChangeTheHistoryTable()
    {
        await using var rig = await TenantRig.StartAsync(
            postgres,
            register: services =>
            {
                services.AddTenantDbContext<AccessContext>("Access", (provider, options) =>
                    options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>(), npgsql => npgsql.MigrationsHistoryTable("evil_history")));
                services.AddTenantMigrator();
            });
        var tenant = Guid.NewGuid();

        var result = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal([AccessItems.Id, AccessCreatedBy.Id], await rig.HistoryAsync(tenant, "Access"));
        Assert.DoesNotContain(await rig.UserTablesAsync(), t => t.EndsWith(".evil_history", StringComparison.Ordinal));
    }
}
