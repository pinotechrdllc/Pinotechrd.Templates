using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Npgsql;

namespace MyApp.Api.Tests.Tenancy;

// Extensions shared by every tenant, the migrator's use of the connection pool, and its lock session. Needs Docker.
[Collection(PostgresContainer.Name)]
public class MigratorExtensionsTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void DocsModule(IServiceCollection services)
    {
        services.AddTenantDbContext<DocsContext>("Docs");
        services.AddTenantMigrator();
    }

    private static IEnumerable<Exception> Chain(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    // Breaks when an extension a model declares is installed in a tenant's schema (the first schema of the path) instead of
    // once in the shared extensions schema, when a second tenant cannot use it, or when a citext column does not compare
    // case-insensitively for both tenants. uuid-ossp and the built-in gen_random_uuid work from a tenant connection.
    [Fact]
    public async Task ADeclaredExtensionIsInstalledOnceInTheExtensionsSchemaAndWorksForTwoTenants()
    {
        await using var rig = await TenantRig.StartAsync(postgres, register: DocsModule);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());

        var results = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync([a, b], 2, Token);

        Assert.All(results, r => Assert.True(r.Succeeded, r.Error?.ToString()));
        Assert.Equal(
            ["citext@extensions", "uuid-ossp@extensions"],
            await rig.ColumnAsync("SELECT extname || '@' || extnamespace::regnamespace::text FROM pg_extension WHERE extname IN ('citext', 'uuid-ossp') ORDER BY 1"));
        foreach (var tenant in new[] { a, b })
        {
            await using var scope = rig.ScopeFor(tenant);
            var context = scope.ServiceProvider.GetRequiredService<DocsContext>();
            context.Docs.Add(new DocItem { Title = "Hello" });
            await context.SaveChangesAsync(Token);

            Assert.Single(await context.Docs.Where(d => d.Title == "HELLO").ToListAsync(Token));
            Assert.Single(await context.Docs.Where(d => d.Title == "hello").ToListAsync(Token));
            Assert.NotEqual(Guid.Empty, await context.Database.SqlQueryRaw<Guid>("SELECT uuid_generate_v4() AS \"Value\"").SingleAsync(Token));
            Assert.NotEqual(Guid.Empty, await context.Database.SqlQueryRaw<Guid>("SELECT gen_random_uuid() AS \"Value\"").SingleAsync(Token));
        }
    }

    // Breaks when an extension pre-installed in public is silently used or reinstalled: a tenant would depend on a schema its
    // connection does not see. The tenant's result fails and the message tells the operator to ALTER EXTENSION.
    [Fact]
    public async Task AnExtensionInstalledInPublicFailsTheTenantResultWithAnAlterExtensionHint()
    {
        await using var rig = await TenantRig.StartAsync(postgres, register: DocsModule);
        await rig.ExecuteAsync("CREATE EXTENSION citext");

        var result = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(Guid.NewGuid(), Token);

        Assert.False(result.Succeeded);
        Assert.Contains(Chain(result.Error), e => e.Message.Contains("ALTER EXTENSION", StringComparison.Ordinal));
    }

    // Breaks when a model that installs an extension in a schema of its own is accepted: the migrator refuses it, naming the
    // module.
    [Fact]
    public async Task AModelDeclaringAnExtensionInAnotherSchemaIsRefused()
    {
        await using var rig = await TenantRig.StartAsync(
            postgres,
            register: services =>
            {
                services.AddTenantDbContext<ExtensionSchemaContext>("ExtSchema");
                services.AddTenantMigrator();
            });

        var result = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(Guid.NewGuid(), Token);

        Assert.False(result.Succeeded);
        var error = Assert.IsType<InvalidOperationException>(result.Error);
        Assert.Contains("ExtSchema", error.Message, StringComparison.Ordinal);
        Assert.False(await rig.SchemaExistsAsync(ExtensionSchemaContext.Schema));
        Assert.DoesNotContain(await rig.UserTablesAsync(), t => t.StartsWith(ExtensionSchemaContext.Schema + ".", StringComparison.Ordinal));
    }

    // Breaks when the shared schema is dropped from the path (a tenant could not see objects there), or put before the
    // tenant's schema (a shared object would hide a tenant table of the same name).
    [Fact]
    public async Task ATenantTableWinsOverAnObjectOfTheSameNameInTheExtensionsSchema()
    {
        await using var rig = await TenantRig.StartAsync(postgres);
        var (withOwn, without) = (Guid.NewGuid(), Guid.NewGuid());
        await rig.ProvisionAsync(withOwn);
        await rig.ProvisionAsync(without);
        await rig.ExecuteAsync(
            "CREATE SCHEMA IF NOT EXISTS extensions; CREATE TABLE extensions.shadow_probe (origin text); "
            + "INSERT INTO extensions.shadow_probe VALUES ('extensions'); "
            + $"CREATE TABLE \"{TenantSchemaName.For(withOwn)}\".shadow_probe (origin text); "
            + $"INSERT INTO \"{TenantSchemaName.For(withOwn)}\".shadow_probe VALUES ('tenant')");

        Assert.Equal("tenant", await Origin(rig, withOwn));
        Assert.Equal("extensions", await Origin(rig, without));
    }

    private static async Task<string> Origin(TenantRig rig, Guid tenant)
    {
        await using var scope = rig.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<AccessContext>().Database
            .SqlQueryRaw<string>("SELECT origin AS \"Value\" FROM shadow_probe").SingleAsync(Token);
    }

    // Breaks when the extension step is not idempotent or not safe under concurrent runs: two migrators (two processes, each
    // with its own state) migrate different tenants of one database at the same time, then a third runs after them.
    [Fact]
    public async Task TheExtensionStepIsIdempotentAndSafeWhenTwoMigratorsRunAtOnce()
    {
        await using var first = await TenantRig.StartAsync(postgres, maxPoolSize: 8, register: DocsModule);
        var connection = postgres.Connection(first.Database);
        connection.MaxPoolSize = 8;
        var second = new NpgsqlDataSourceBuilder(connection.ConnectionString).Build();
        var secondRig = TenantRig.StartOn(connection.ConnectionString, second, ownsDataSource: true, DocsModule);
        await using var secondDisposer = secondRig;

        var one = first.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()), 4, Token);
        var two = secondRig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()), 4, Token);
        var results = (await Task.WhenAll(one, two)).SelectMany(r => r).ToList();

        Assert.Equal(8, results.Count);
        Assert.All(results, r => Assert.True(r.Succeeded, r.Error?.ToString()));
        Assert.Equal(1, await first.ScalarAsync<long>("SELECT count(*) FROM pg_extension WHERE extname = 'citext'"));
        Assert.Equal("extensions", await first.ScalarAsync<string>("SELECT extnamespace::regnamespace::text FROM pg_extension WHERE extname = 'citext'"));

        var again = await secondRig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(Guid.NewGuid(), Token);
        Assert.True(again.Succeeded, again.Error?.ToString());
    }
}

[Collection(PostgresContainer.Name)]
public class MigratorCapacityTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Breaks when a batch on a tiny pool exhausts it: each tenant in progress holds two connections, so on a pool of two the
    // migrator must run one tenant at a time even when asked for two.
    [Fact]
    public async Task ThreeTenantsOnAPoolOfTwoWithDegreeTwoAllSucceed()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);

        var results = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()), 2, Token);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.Succeeded, r.Error?.ToString()));
    }

    // Breaks when many single-tenant calls at once can use more connections than the pool holds (they would time out
    // waiting for each other).
    [Fact]
    public async Task TwelveConcurrentSingleTenantCallsOnAPoolOfTwoNeverExhaustIt()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var migrator = rig.Services.GetRequiredService<ITenantMigrator>();

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => migrator.MigrateAsync(Guid.NewGuid(), Token), Token)))
            .WaitAsync(TimeSpan.FromMinutes(2), Token);

        Assert.All(results, r => Assert.True(r.Succeeded, r.Error?.ToString()));
    }

    // Breaks when a pool too small for one tenant (it needs two connections) is accepted and then deadlocks or times out.
    [Fact]
    public async Task AMigratorOverAPoolOfOneIsRefusedWithAClearMessage()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 1);

        var error = await Record.ExceptionAsync(async () =>
            await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(Guid.NewGuid(), Token));

        var refusal = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("Maximum Pool Size", refusal.Message, StringComparison.Ordinal);
    }

    // Breaks when a requested degree above the capacity is honoured: with a pool of seven the migrator runs three tenants at
    // once (seven connections: three tenants of two, one left over), so no more than three tenant locks are ever held, even
    // when eight tenants and a degree of eight are asked for and every tenant is blocked inside its migration.
    [Fact]
    public async Task ADegreeLargerThanTheCapacityIsClamped()
    {
        await using var rig = await TenantRig.StartAsync(
            postgres,
            maxPoolSize: 7,
            register: services =>
            {
                services.AddTenantDbContext<GateContext>("Gate");
                services.AddTenantMigrator();
            });
        await using var gate = await rig.OpenAdminAsync();
        var holderPid = gate.ProcessID;
        await using (var hold = new NpgsqlCommand($"SELECT pg_advisory_lock({GateContext.GateKey})", gate))
        {
            await hold.ExecuteNonQueryAsync(Token);
        }

        var run = rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()), 8, Token);
        var peak = 0L;
        try
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(3))
            {
                peak = Math.Max(
                    peak,
                    await rig.ScalarAsync<long>(
                        "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND granted "
                        + $"AND pid <> {holderPid} AND database = (SELECT oid FROM pg_database WHERE datname = current_database())"));
                await Task.Delay(50, Token);
            }
        }
        finally
        {
            await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({GateContext.GateKey})", gate);
            await release.ExecuteNonQueryAsync(Token);
        }

        var results = await run.WaitAsync(TimeSpan.FromMinutes(2), Token);
        Assert.Equal(3, peak);
        Assert.All(results, r => Assert.True(r.Succeeded, r.Error?.ToString()));
    }
}

// The session that holds a tenant's lock while its modules migrate.
[Collection(PostgresContainer.Name)]
public class MigratorLockSessionTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static IEnumerable<Exception> Chain(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    // Breaks when the lock session is left exposed to the server's idle session limit: with idle_session_timeout of one
    // second and a first module whose migration takes three seconds, the idle lock session would be ended, the lock lost,
    // and the second module refused.
    [Fact]
    public async Task AnIdleSessionTimeoutDoesNotEndTheLockSessionDuringALongMigration()
    {
        await using var rig = await TenantRig.StartAsync(
            postgres,
            register: services =>
            {
                services.AddTenantDbContext<SlowContext>("Access");
                services.AddTenantDbContext<BillingContext>("Billing");
                services.AddTenantMigrator();
            });
        await rig.ExecuteAsync($"ALTER DATABASE \"{rig.Database}\" SET idle_session_timeout = '1s'");
        Assert.Equal("1s", await rig.ScalarAsync<string>("SHOW idle_session_timeout"));
        var tenant = Guid.NewGuid();

        var result = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal([BillingItems.Id, BillingCreatedBy.Id], await rig.HistoryAsync(tenant, "Billing"));
    }

    // Breaks when a run goes on after its lock session ended: the lock is gone, so another run could migrate the same tenant.
    // The session is terminated while the first module's migration is blocked; when it finishes, the result fails with the
    // lost-lock message and the second module never runs.
    [Fact]
    public async Task ALostLockSessionStopsTheTenantBeforeTheNextModule()
    {
        await using var rig = await TenantRig.StartAsync(
            postgres,
            maxPoolSize: 6,
            register: services =>
            {
                services.AddTenantDbContext<GateContext>("Access");
                services.AddTenantDbContext<BillingContext>("Billing");
                services.AddTenantMigrator();
            });
        var tenant = Guid.NewGuid();
        await using var gate = await rig.OpenAdminAsync();
        var holderPid = gate.ProcessID;
        await using (var hold = new NpgsqlCommand($"SELECT pg_advisory_lock({GateContext.GateKey})", gate))
        {
            await hold.ExecuteNonQueryAsync(Token);
        }

        var run = rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token);
        try
        {
            var lockPid = 0;
            var watch = Stopwatch.StartNew();
            while (lockPid == 0)
            {
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "The run never reached its first migration.");
                var blocked = await rig.ScalarAsync<long>(
                    "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted "
                    + "AND database = (SELECT oid FROM pg_database WHERE datname = current_database())");
                if (blocked > 0)
                {
                    lockPid = (int)await rig.ScalarAsync<int>(
                        "SELECT pid FROM pg_locks WHERE locktype = 'advisory' AND granted "
                        + $"AND pid <> {holderPid} AND database = (SELECT oid FROM pg_database WHERE datname = current_database())");
                }
                else
                {
                    await Task.Delay(50, Token);
                }
            }

            Assert.True(await rig.ScalarAsync<bool>($"SELECT pg_terminate_backend({lockPid})"));
        }
        finally
        {
            await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({GateContext.GateKey})", gate);
            await release.ExecuteNonQueryAsync(Token);
        }

        var result = await run.WaitAsync(TimeSpan.FromSeconds(60), Token);

        Assert.False(result.Succeeded);
        Assert.Contains(Chain(result.Error), e => e.Message.Contains("migration lock", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Modules, m => m.ModuleName == "Billing" && m.Succeeded);
        Assert.DoesNotContain(await rig.UserTablesAsync(), t => t.EndsWith(".billing_items", StringComparison.Ordinal));
    }
}
