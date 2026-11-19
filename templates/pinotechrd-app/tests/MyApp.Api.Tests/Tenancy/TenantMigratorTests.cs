using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Npgsql;

namespace MyApp.Api.Tests.Tenancy;

// The migrator against a real server: it creates the schema, applies each module's migrations in a fixed order, takes a
// lock per tenant, and reports a failed tenant without stopping the others. Needs Docker.
[Collection(PostgresContainer.Name)]
public class TenantMigratorTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string[] AccessIds = [AccessItems.Id, AccessCreatedBy.Id];
    private static readonly string[] BillingIds = [BillingItems.Id, BillingCreatedBy.Id];
    private static readonly string[] SalesIds = [SalesItems.Id, SalesCreatedBy.Id];

    // Breaks when the migrator does not create a missing schema, applies modules in registration order (Sales, Billing,
    // Access here) instead of Access first and then by name, applies a module's migrations out of order, or reports
    // something other than what it applied.
    [Fact]
    public async Task ItCreatesAMissingSchemaAndAppliesEveryModulesMigrationsAccessFirstThenByName()
    {
        await using var rig = await TenantRig.StartAsync(postgres);
        var tenant = Guid.NewGuid();
        Assert.False(await rig.SchemaExistsAsync(TenantSchemaName.For(tenant)));

        var result = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(tenant, result.TenantId);
        Assert.Equal(TenantSchemaName.For(tenant), result.SchemaName);
        Assert.True(result.SchemaCreated);
        Assert.Equal(["Access", "Billing", "Sales"], result.Modules.Select(m => m.ModuleName));
        Assert.All(result.Modules, m => Assert.True(m.Succeeded));
        Assert.Equal(AccessIds, result.Modules[0].AppliedMigrations);
        Assert.Equal(BillingIds, result.Modules[1].AppliedMigrations);
        Assert.Equal(SalesIds, result.Modules[2].AppliedMigrations);
        Assert.Equal(AccessIds, await rig.HistoryAsync(tenant, "Access"));
        Assert.Equal(BillingIds, await rig.HistoryAsync(tenant, "Billing"));
        Assert.Equal(SalesIds, await rig.HistoryAsync(tenant, "Sales"));
        Assert.Equal(
            [
                $"{TenantSchemaName.For(tenant)}.__ef_migrations_access",
                $"{TenantSchemaName.For(tenant)}.__ef_migrations_billing",
                $"{TenantSchemaName.For(tenant)}.__ef_migrations_sales",
                $"{TenantSchemaName.For(tenant)}.access_items",
                $"{TenantSchemaName.For(tenant)}.billing_items",
                $"{TenantSchemaName.For(tenant)}.sales_items",
            ],
            await rig.UserTablesAsync());
    }

    // Breaks when the order is not ordinal and Access-first: Access leads even when another name sorts before it, and the
    // rest follow by ordinal comparison (upper case before lower case, which a culture comparison would not give).
    [Fact]
    public async Task ModulesRunAccessFirstThenByOrdinalName()
    {
        await using var rig = await TenantRig.StartAsync(
            postgres,
            register: services =>
            {
                services.AddTenantDbContext<AlphaContext>("alpha");
                services.AddTenantDbContext<ZuluContext>("Zulu");
                services.AddTenantDbContext<BillingContext>("Billing");
                services.AddTenantDbContext<AaaContext>("Aaa");
                services.AddTenantDbContext<AccessContext>("Access");
                services.AddTenantMigrator();
            });

        var result = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(Guid.NewGuid(), Token);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(["Access", "Aaa", "Billing", "Zulu", "alpha"], result.Modules.Select(m => m.ModuleName));
    }

    // Breaks when a second run reapplies, re-creates or reports anything: nothing is pending.
    [Fact]
    public async Task ASecondRunAppliesNothing()
    {
        await using var rig = await TenantRig.StartAsync(postgres);
        var tenant = Guid.NewGuid();
        var migrator = rig.Services.GetRequiredService<ITenantMigrator>();
        Assert.True((await migrator.MigrateAsync(tenant, Token)).Succeeded);

        var second = await migrator.MigrateAsync(tenant, Token);

        Assert.True(second.Succeeded, second.Error?.ToString());
        Assert.False(second.SchemaCreated);
        Assert.Equal(["Access", "Billing", "Sales"], second.Modules.Select(m => m.ModuleName));
        Assert.All(second.Modules, m => Assert.Empty(m.AppliedMigrations));
        Assert.Equal(AccessIds, await rig.HistoryAsync(tenant, "Access"));
        Assert.Equal(SalesIds, await rig.HistoryAsync(tenant, "Sales"));
    }

    // Breaks when a pending migration of a module that already has some is skipped, or its applied ones are repeated.
    [Fact]
    public async Task OnlyThePendingMigrationsAreAppliedWhenSomeAreAlreadyThere()
    {
        await using var rig = await TenantRig.StartAsync(postgres);
        var tenant = Guid.NewGuid();
        await rig.CreateTenantSchemaAsync(tenant, allowDdl: true);
        await using (var scope = rig.ScopeFor(tenant))
        {
            var access = scope.ServiceProvider.GetRequiredService<AccessContext>();
            await access.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync(AccessItems.Id, Token);
        }

        var result = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.False(result.SchemaCreated);
        Assert.Equal([AccessCreatedBy.Id], result.Modules.Single(m => m.ModuleName == "Access").AppliedMigrations);
        Assert.Equal(AccessIds, await rig.HistoryAsync(tenant, "Access"));
    }

    // Breaks when concurrent runs on one tenant are not serialised by the lock: duplicate schema creation, a migration
    // applied twice, or a failure. Ten runs on a pool of four connections: each waiting run must give its connection
    // back, or the pool is exhausted and the run holding the lock cannot work.
    [Fact]
    public async Task TenConcurrentRunsOnOneTenantOnAPoolOfFourAllSucceedAndApplyEachMigrationOnce()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 4);
        var tenant = Guid.NewGuid();
        var migrator = rig.Services.GetRequiredService<ITenantMigrator>();
        using var start = new ManualResetEventSlim(false);

        var runs = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() =>
            {
                start.Wait(Token);
                return migrator.MigrateAsync(tenant, Token);
            }, Token))
            .ToList();
        start.Set();
        var results = await Task.WhenAll(runs).WaitAsync(TimeSpan.FromMinutes(2), Token);

        Assert.All(results, r => Assert.True(r.Succeeded, r.Error?.ToString()));
        Assert.Equal(1, results.Count(r => r.SchemaCreated));
        foreach (var (module, ids) in new[] { ("Access", AccessIds), ("Billing", BillingIds), ("Sales", SalesIds) })
        {
            var applied = results.SelectMany(r => r.Modules.Single(m => m.ModuleName == module).AppliedMigrations).ToList();
            Assert.Equal(ids, applied.Order(StringComparer.Ordinal));
            Assert.Equal(ids, await rig.HistoryAsync(tenant, module));
        }
    }

    // Breaks when a tenant that fails stops the batch or hides its error, or when the failed tenant's partial state is
    // reported as success. The failing tenant has a table of the Access module already, with no history.
    [Fact]
    public async Task AFailingTenantIsReportedAndTheOthersAreStillMigrated()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 4);
        var (first, broken, last) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var brokenSchema = TenantSchemaName.For(broken);
        await rig.ExecuteAsync($"CREATE SCHEMA \"{brokenSchema}\"; CREATE TABLE \"{brokenSchema}\".access_items (id integer)");
        var migrator = rig.Services.GetRequiredService<ITenantMigrator>();

        var results = await migrator.MigrateAsync([first, broken, last], 2, Token);

        Assert.Equal([first, broken, last], results.Select(r => r.TenantId));
        Assert.True(results[0].Succeeded, results[0].Error?.ToString());
        Assert.True(results[2].Succeeded, results[2].Error?.ToString());
        Assert.False(results[1].Succeeded);
        var error = Assert.IsAssignableFrom<Exception>(results[1].Error);
        Assert.Contains(Flatten(error), e => e is PostgresException { SqlState: "42P07" });
        var stopped = Assert.Single(results[1].Modules);
        Assert.Equal("Access", stopped.ModuleName);
        Assert.False(stopped.Succeeded);
        Assert.Equal(AccessIds, await rig.HistoryAsync(first, "Access"));
        Assert.Equal(SalesIds, await rig.HistoryAsync(last, "Sales"));
        Assert.DoesNotContain(await rig.UserTablesAsync(), t => t.StartsWith(brokenSchema + ".billing", StringComparison.Ordinal));

        // The failed run left no lock behind: once the conflict is gone the same tenant migrates.
        await rig.ExecuteAsync($"DROP TABLE \"{brokenSchema}\".access_items");
        var retried = await migrator.MigrateAsync(broken, Token);
        Assert.True(retried.Succeeded, retried.Error?.ToString());
        Assert.Equal(SalesIds, await rig.HistoryAsync(broken, "Sales"));
    }

    // Breaks when the migrator or the module contract needs a web host: a plain service collection with no ASP.NET Core
    // service (and no host at all) runs a migration end to end.
    [Fact]
    public async Task TheMigratorRunsOnAPlainServiceCollectionWithNoWebServices()
    {
        IServiceCollection? registered = null;
        await using var rig = await TenantRig.StartAsync(
            postgres,
            register: services =>
            {
                TenantRig.StandardModules(services);
                registered = services;
            });
        var tenant = Guid.NewGuid();

        var result = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(AccessIds, await rig.HistoryAsync(tenant, "Access"));
        Assert.NotNull(registered);
        Assert.DoesNotContain(
            registered,
            d => (d.ServiceType.Namespace ?? string.Empty).StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                || (d.ImplementationType?.Namespace ?? string.Empty).StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.DoesNotContain(
            registered,
            d => d.ServiceType.Name is "IWebHostEnvironment" or "IHostEnvironment" or "IConfiguration");
    }

    // Breaks when a batch migrates a repeated id twice, loses the order of the ids, or accepts an empty id (and migrates
    // the valid ones before refusing it).
    [Fact]
    public async Task ABatchReportsOneResultPerDistinctTenantInOrderAndRefusesAnEmptyIdBeforeStarting()
    {
        await using var rig = await TenantRig.StartAsync(postgres);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var migrator = rig.Services.GetRequiredService<ITenantMigrator>();

        var results = await migrator.MigrateAsync([b, a, b], 3, Token);

        Assert.Equal([b, a], results.Select(r => r.TenantId));
        Assert.All(results, r => Assert.True(r.Succeeded, r.Error?.ToString()));

        var untouched = Guid.NewGuid();
        await Assert.ThrowsAsync<ArgumentException>(() => migrator.MigrateAsync([untouched, Guid.Empty], 2, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => migrator.MigrateAsync(Guid.Empty, Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => migrator.MigrateAsync([untouched], 0, Token));
        Assert.False(await rig.SchemaExistsAsync(TenantSchemaName.For(untouched)));
    }

    // Breaks when a waiter for a tenant's lock ignores its cancellation token (it would wait for the run that holds the
    // lock), or when it does not wait for the lock at all. The migration of the Gate module blocks on an advisory lock
    // that the test holds, which keeps one run inside the tenant's lock for as long as the test wants.
    [Fact]
    public async Task CancellingARunThatWaitsForTheTenantLockStopsTheWaitAndNotTheRunThatHoldsIt()
    {
        await using var rig = await TenantRig.StartAsync(
            postgres,
            maxPoolSize: 4,
            register: services =>
            {
                services.AddTenantDbContext<GateContext>("Gate");
                services.AddTenantMigrator();
            });
        var tenant = Guid.NewGuid();
        var migrator = rig.Services.GetRequiredService<ITenantMigrator>();
        await using var gate = await rig.OpenAdminAsync();
        await using (var hold = new NpgsqlCommand($"SELECT pg_advisory_lock({GateContext.GateKey})", gate))
        {
            await hold.ExecuteNonQueryAsync(Token);
        }

        var holder = migrator.MigrateAsync(tenant, Token);
        try
        {
            await WaitForBlockedMigrationsAsync(rig, 1);
            using var cancel = new CancellationTokenSource();
            var waiter = migrator.MigrateAsync(tenant, cancel.Token);
            await Task.Delay(700, Token);
            Assert.False(waiter.IsCompleted, "The second run did not wait for the tenant's lock.");
            Assert.Equal(1, await BlockedMigrationsAsync(rig));
            var watch = Stopwatch.StartNew();

            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.WaitAsync(TimeSpan.FromSeconds(10), Token));

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"The cancelled wait took {watch.Elapsed}.");
            Assert.False(holder.IsCompleted, "Cancelling the waiter stopped the run that holds the lock.");
        }
        finally
        {
            await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({GateContext.GateKey})", gate);
            await release.ExecuteNonQueryAsync(Token);
        }

        var finished = await holder.WaitAsync(TimeSpan.FromSeconds(30), Token);
        Assert.True(finished.Succeeded, finished.Error?.ToString());
        Assert.Equal([GateWait.Id], await rig.HistoryAsync(tenant, "Gate"));
    }

    private static Task<long> BlockedMigrationsAsync(TenantRig rig) =>
        rig.ScalarAsync<long>(
            "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted "
            + "AND database = (SELECT oid FROM pg_database WHERE datname = current_database())");

    private static async Task WaitForBlockedMigrationsAsync(TenantRig rig, int expected)
    {
        var watch = Stopwatch.StartNew();
        while (await BlockedMigrationsAsync(rig) < expected)
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "The first run never reached its migration.");
            await Task.Delay(50, Token);
        }
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            yield return current;
            if (current.InnerException is null)
            {
                yield break;
            }
        }
    }

    // Breaks when a context whose model names a schema anywhere (default schema, table, view, split table, function,
    // sequence) is migrated: every tenant's objects would go to that one schema. The failure is reported for the tenant,
    // naming the module, and nothing is created in that schema.
    [Theory]
    [InlineData("DefaultSchema", DefaultSchemaContext.Schema)]
    [InlineData("TableSchema", TableSchemaContext.Schema)]
    [InlineData("ViewSchema", "custom_view_schema")]
    [InlineData("SplitSchema", "custom_split_schema")]
    [InlineData("FunctionSchema", FunctionSchemaContext.Schema)]
    [InlineData("SequenceSchema", SequenceSchemaContext.Schema)]
    public async Task AModuleWhoseModelNamesASchemaIsRefusedAndNothingIsCreatedInThatSchema(string module, string schema)
    {
        await using var rig = await TenantRig.StartAsync(
            postgres,
            register: services =>
            {
                switch (module)
                {
                    case "DefaultSchema":
                        services.AddTenantDbContext<DefaultSchemaContext>(module);
                        break;
                    case "TableSchema":
                        services.AddTenantDbContext<TableSchemaContext>(module);
                        break;
                    case "ViewSchema":
                        services.AddTenantDbContext<ViewSchemaContext>(module);
                        break;
                    case "SplitSchema":
                        services.AddTenantDbContext<SplitSchemaContext>(module);
                        break;
                    case "FunctionSchema":
                        services.AddTenantDbContext<FunctionSchemaContext>(module);
                        break;
                    default:
                        services.AddTenantDbContext<SequenceSchemaContext>(module);
                        break;
                }

                services.AddTenantMigrator();
            });
        var tenant = Guid.NewGuid();

        var result = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token);

        Assert.False(result.Succeeded);
        var error = Assert.IsType<InvalidOperationException>(result.Error);
        Assert.Contains(module, error.Message, StringComparison.Ordinal);
        Assert.Contains("schema", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await rig.SchemaExistsAsync(schema));
        Assert.DoesNotContain(await rig.UserTablesAsync(), t => t.StartsWith(schema + ".", StringComparison.Ordinal));
    }
}
