using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Application.Tenancy;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Npgsql;

namespace MyApp.Api.Tests.Tenancy;

// Probes of ways a tenant context can be used that the documentation does not show, kept as tests where the behaviour is
// safe. Where a probe found something to report, the test says so.
public class TenantUsageProbeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static IHost Host(string environment)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment });
        builder.Services.AddSingleton(new NpgsqlDataSourceBuilder(TestDatabase.PlaceholderConnectionString).Build());
        builder.Services.AddTenantDbContext<AccessContext>("Access");
        return builder.Build();
    }

    // Breaks when a tenant context can be taken from the root provider in Development (where the host validates scopes):
    // a context outside a scope would share one tenant for the life of the process.
    [Fact]
    public void InDevelopmentTheRootProviderRefusesATenantContext()
    {
        using var host = Host(Environments.Development);

        Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<AccessContext>());
    }

    // Outside Development the host does not validate scopes, so a context can be resolved from the root provider. It is
    // then bound to the root's own tenant holder, which no request sets: it fails closed and reaches no server.
    // Breaks when such a context opens a connection with no tenant (a fallback to public).
    [Fact]
    public async Task OutsideDevelopmentAContextTakenFromTheRootProviderFailsClosedWithoutATenant()
    {
        using var host = Host(Environments.Production);
        var context = host.Services.GetRequiredService<AccessContext>();

        Assert.False(host.Services.GetRequiredService<ICurrentTenant>().IsSet);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Items.ToListAsync(Token));
        Assert.Throws<InvalidOperationException>(() => context.Items.ToList());
    }
}

// Several contexts in one scope, a transaction across them, and a scope whose tenant is set late. Needs Docker.
[Collection(PostgresContainer.Name)]
public class TenantScopeProbeTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Breaks when two contexts of one scope do not share the scope's tenant, or when one module's migrations are recorded in
    // another module's history table.
    [Fact]
    public async Task TwoContextsInOneScopeShareTheTenantAndEachUsesItsOwnHistoryTable()
    {
        await using var rig = await TenantRig.StartAsync(postgres);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        var schema = TenantSchemaName.For(tenant);

        await using (var scope = rig.ScopeFor(tenant))
        {
            var access = scope.ServiceProvider.GetRequiredService<AccessContext>();
            var billing = scope.ServiceProvider.GetRequiredService<BillingContext>();
            Assert.NotSame(access, billing);
            Assert.Equal(schema, await access.Database.SqlQueryRaw<string>("SELECT current_schema() AS \"Value\"").SingleAsync(Token));
            Assert.Equal(schema, await billing.Database.SqlQueryRaw<string>("SELECT current_schema() AS \"Value\"").SingleAsync(Token));
            access.Items.Add(new FixtureItem { Name = "from-access" });
            billing.Items.Add(new FixtureItem { Name = "from-billing" });
            await access.SaveChangesAsync(Token);
            await billing.SaveChangesAsync(Token);
        }

        Assert.Equal(["from-access"], await rig.ColumnAsync($"SELECT name FROM \"{schema}\".access_items"));
        Assert.Equal(["from-billing"], await rig.ColumnAsync($"SELECT name FROM \"{schema}\".billing_items"));
        Assert.Equal([AccessItems.Id, AccessCreatedBy.Id], await rig.HistoryAsync(tenant, "Access"));
        Assert.Equal([BillingItems.Id, BillingCreatedBy.Id], await rig.HistoryAsync(tenant, "Billing"));
        Assert.DoesNotContain(await rig.HistoryAsync(tenant, "Access"), id => id.Contains("Billing", StringComparison.Ordinal));
    }

    // Each context of a scope has a connection of its own, so Database.UseTransaction alone cannot span two of them (EF
    // refuses a transaction of another connection). A transaction across two contexts needs one shared connection: the
    // first context opens it (its interceptor selects the tenant's schema) and the second adopts it with SetDbConnection.
    // Both writes then commit together, or neither does.
    // Breaks when the adopted connection is not on the tenant's schema (the second context's writes would fail or go
    // elsewhere), or when the rollback keeps one side.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATransactionSpanningTwoContextsOfOneScopeOnASharedConnectionCommitsOrRollsBackBoth(bool commit)
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        var schema = TenantSchemaName.For(tenant);

        await using (var scope = rig.ScopeFor(tenant))
        {
            var access = scope.ServiceProvider.GetRequiredService<AccessContext>();
            var billing = scope.ServiceProvider.GetRequiredService<BillingContext>();
            await access.Database.OpenConnectionAsync(Token);
            billing.Database.SetDbConnection(access.Database.GetDbConnection());
            await using var transaction = await access.Database.BeginTransactionAsync(Token);
            await billing.Database.UseTransactionAsync(transaction.GetDbTransaction(), Token);
            access.Items.Add(new FixtureItem { Name = "tx-access" });
            billing.Items.Add(new FixtureItem { Name = "tx-billing" });
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

        var expectedAccess = commit ? new[] { "tx-access" } : [];
        var expectedBilling = commit ? new[] { "tx-billing" } : [];
        Assert.Equal(expectedAccess, await rig.ColumnAsync($"SELECT name FROM \"{schema}\".access_items"));
        Assert.Equal(expectedBilling, await rig.ColumnAsync($"SELECT name FROM \"{schema}\".billing_items"));
    }

    // Breaks when the documented limit disappears silently: a transaction started on one context cannot be handed to a
    // context on a connection of its own (EF refuses it), so no write can half-commit through that mistake.
    [Fact]
    public async Task ATransactionOfOneContextCannotBeUsedByAnotherContextOfTheScope()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        await using var scope = rig.ScopeFor(tenant);
        var access = scope.ServiceProvider.GetRequiredService<AccessContext>();
        var billing = scope.ServiceProvider.GetRequiredService<BillingContext>();
        await using var transaction = await access.Database.BeginTransactionAsync(Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => billing.Database.UseTransactionAsync(transaction.GetDbTransaction(), Token));
    }

    // A long-running scope may resolve its context before the tenant is known. The tenant is read each time a connection
    // opens, so initialising it late works for the first use, and the context's use before that fails closed.
    // Breaks when the context captures the tenant (or its absence) when it is resolved.
    [Fact]
    public async Task AContextResolvedBeforeTheTenantIsSetWorksAfterTheTenantIsInitialisedAndNotBefore()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        await using var scope = rig.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Items.ToListAsync(Token));

        scope.ServiceProvider.GetRequiredService<ICurrentTenantInitializer>().Initialize(tenant);
        context.Items.Add(new FixtureItem { Name = "late" });
        await context.SaveChangesAsync(Token);

        Assert.Equal(["late"], await context.Items.Select(i => i.Name).ToListAsync(Token));
        Assert.Equal(["late"], await rig.ColumnAsync($"SELECT name FROM \"{TenantSchemaName.For(tenant)}\".access_items"));
    }

    // Once a scope has worked for a tenant, a refused switch keeps the context on that tenant: the context never drifts to
    // the tenant that was refused.
    // Breaks when a refused Initialize still changes what the context's next connection selects.
    [Fact]
    public async Task ARefusedTenantSwitchLeavesTheContextOnTheFirstTenant()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        await rig.ProvisionAsync(a);
        await rig.ProvisionAsync(b);
        await using var scope = rig.ScopeFor(a);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
        context.Items.Add(new FixtureItem { Name = "first" });
        await context.SaveChangesAsync(Token);

        Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<ICurrentTenantInitializer>().Initialize(b));
        context.Items.Add(new FixtureItem { Name = "second" });
        await context.SaveChangesAsync(Token);

        Assert.Equal(["first", "second"], await rig.ColumnAsync($"SELECT name FROM \"{TenantSchemaName.For(a)}\".access_items ORDER BY name"));
        Assert.Empty(await rig.ColumnAsync($"SELECT name FROM \"{TenantSchemaName.For(b)}\".access_items"));
    }

    // Registering a pooled context for a tenant context's type after AddTenantDbContext must not turn pooling on and
    // lose the tenant selection: two tenants in turn on the same type still see their own schemas.
    // Breaks when a later AddDbContextPool replaces the tenant context's options (no interceptor, a shared instance).
    [Fact]
    public async Task APooledRegistrationAddedAfterTheTenantContextDoesNotLoseTheTenantSelection()
    {
        await using var rig = await TenantRig.StartAsync(
            postgres,
            maxPoolSize: 2,
            register: services =>
            {
                services.AddTenantDbContext<AccessContext>("Access");
                services.AddTenantMigrator();
                services.AddDbContextPool<AccessContext>((provider, options) =>
                    options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));
            });
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        await rig.ProvisionAsync(a);
        await rig.ProvisionAsync(b);

        foreach (var tenant in new[] { a, b, a, b })
        {
            await using var scope = rig.ScopeFor(tenant);
            var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
            Assert.Equal(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>("SELECT current_schema() AS \"Value\"").SingleAsync(Token));
        }
    }
}
