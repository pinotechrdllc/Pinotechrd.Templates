using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Application.Tenancy;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;

namespace MyApp.Api.Tests.Tenancy;

// What a tenant context refuses: no tenant, and a schema name that is not valid. In both cases nothing reaches the
// server. Needs Docker, to prove that nothing was sent.
[Collection(PostgresContainer.Name)]
public class TenantRefusalTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void SyncAttempts(AccessContext context)
    {
        Assert.Throws<InvalidOperationException>(() => context.Items.ToList());
        Assert.Throws<InvalidOperationException>(() => context.Database.OpenConnection());
        context.Items.Add(new FixtureItem { Name = "never-written-sync" });
        Assert.Throws<InvalidOperationException>(() => context.SaveChanges());
    }

    // Breaks when a context without a current tenant opens a connection anyway (a fallback to public or to the default
    // search_path), whichever way it is used: save, query, sync open, migrate. Nothing may be written, no schema may be
    // created, and no command may reach the server.
    [Fact]
    public async Task WithNoCurrentTenantEveryWayOfOpeningFailsAndNothingIsWritten()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        await using var scope = rig.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
        var before = rig.CommandsSent;

        context.Items.Add(new FixtureItem { Name = "never-written" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync(Token));
        context.ChangeTracker.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Items.ToListAsync(Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.OpenConnectionAsync(Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.MigrateAsync(Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.GetPendingMigrationsAsync(Token));
        context.ChangeTracker.Clear();
        SyncAttempts(context);

        Assert.Equal(before, rig.CommandsSent);
        Assert.Equal(0, await rig.ScalarAsync<long>("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid()"));
        Assert.Empty(await rig.UserTablesAsync());
        Assert.Equal(0, await rig.ScalarAsync<long>("SELECT count(*) FROM pg_namespace WHERE nspname LIKE 'tenant\\_%'"));
        Assert.False(scope.ServiceProvider.GetRequiredService<ICurrentTenant>().IsSet);
    }

    // Breaks when a refusal leaves the context unusable for the tenant that is set afterwards (the tenant is read when a
    // connection opens, not when the context is built).
    [Fact]
    public async Task ARefusedOpenDoesNotPreventTheContextFromWorkingOnceTheTenantIsSet()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        await using var scope = rig.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Items.ToListAsync(Token));

        scope.ServiceProvider.GetRequiredService<ICurrentTenantInitializer>().Initialize(tenant);
        context.Items.Add(new FixtureItem { Name = "after-initialize" });
        await context.SaveChangesAsync(Token);

        Assert.Equal(["after-initialize"], await rig.ColumnAsync($"SELECT name FROM \"{TenantSchemaName.For(tenant)}\".access_items"));
    }

    // Breaks when a schema name outside ^[a-z][a-z0-9_]{0,62}$ is sent to the server (as a parameter, quoted or
    // concatenated) instead of being rejected first. The same context, the same rig: after each rejection the server has
    // seen no connection and no command, and a control command proves that the counter would have seen one.
    [Fact]
    public async Task AnInvalidSchemaNameIsRejectedBeforeAnySqlIsSent()
    {
        var holder = new SchemaHolder();
        await using var rig = await TenantRig.StartAsync(
            postgres,
            maxPoolSize: 2,
            register: services =>
            {
                services.AddSingleton(holder);
                services.AddScoped<ICurrentTenant, ClaimedTenant>();
                services.AddTenantDbContext<AccessContext>("Access");
            });
        var before = rig.CommandsSent;

        foreach (var name in TenantSchemaNameTests.HostileNames.Select(n => n ?? string.Empty))
        {
            holder.Schema = name;
            await using var scope = rig.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AccessContext>();

            await Assert.ThrowsAsync<ArgumentException>(() => context.Items.ToListAsync(Token));
            context.Items.Add(new FixtureItem { Name = "never-written" });
            await Assert.ThrowsAsync<ArgumentException>(() => context.SaveChangesAsync(Token));
            context.ChangeTracker.Clear();
            Assert.Throws<ArgumentException>(() => context.Items.ToList());
            Assert.Throws<ArgumentException>(() => context.Database.OpenConnection());
        }

        Assert.Equal(before, rig.CommandsSent);
        Assert.Equal(0, await rig.ScalarAsync<long>("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid()"));
        await using (var control = rig.DataSource.CreateCommand("SELECT 1"))
        {
            await control.ExecuteScalarAsync(Token);
        }

        Assert.True(await TelemetryUntilAsync(() => rig.CommandsSent > before), "The command counter did not see the control command.");
    }

    // Breaks when a valid name that is not a tenant's (here a pre-existing schema) is refused: the check is the pattern,
    // not an allow-list of tenant_ names, but the tenant schema is what the production tenant gives.
    [Fact]
    public async Task AValidSchemaNameIsAccepted()
    {
        var holder = new SchemaHolder { Schema = "claimed_valid_schema" };
        await using var rig = await TenantRig.StartAsync(
            postgres,
            maxPoolSize: 2,
            register: services =>
            {
                services.AddSingleton(holder);
                services.AddScoped<ICurrentTenant, ClaimedTenant>();
                services.AddTenantDbContext<AccessContext>("Access");
            });
        await rig.ExecuteAsync("CREATE SCHEMA claimed_valid_schema");
        await using var scope = rig.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();

        var schema = await context.Database.SqlQueryRaw<string>("SELECT current_schema() AS \"Value\"").SingleAsync(Token);

        Assert.Equal("claimed_valid_schema", schema);
    }

    private static async Task<bool> TelemetryUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            await Task.Delay(20, Token);
        }

        return true;
    }
}
