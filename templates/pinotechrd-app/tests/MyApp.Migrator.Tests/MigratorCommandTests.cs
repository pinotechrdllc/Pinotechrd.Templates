using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Infrastructure.Migrations;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.Migrator.Tests.Support;
using MyApp.Modules.Access.Infrastructure.Persistence;
using Npgsql;

namespace MyApp.Migrator.Tests;

// The migrator command against a real PostgreSQL server (Docker): the shared catalog first, then every tenant of the
// catalog, all under one global lock. Black box: the command is run in process with its arguments, and the database and
// the report are read.
[Collection(PostgresContainer.Name)]
public partial class MigratorCommandTests(PostgresFixture postgres)
{
    private static readonly string[] CatalogTables =
    [
        "__ef_migrations_catalog",
        "subscription_plan_features",
        "subscription_plan_modules",
        "subscription_plans",
        "system_subscriptions",
        "tenants",
    ];

    private static readonly string[] CatalogTableNames = [.. CatalogTables.Where(t => !t.StartsWith("__", StringComparison.Ordinal))];

    private const string HistoryOfCatalog = "SELECT migration_id FROM catalog.__ef_migrations_catalog ORDER BY migration_id";

    // The tables the Access context migrates into every tenant schema, read from its model and not typed here.
    private static List<string> AccessTenantTables()
    {
        using var context = DesignContexts.Create<AccessDbContext>();
        return [.. context.GetService<IDesignTimeModel>().Model.GetRelationalModel().Tables
            .Where(t => !t.IsExcludedFromMigrations)
            .Select(t => t.Name)
            .Append(TenantPersistenceRegistration.HistoryTableOf("Access"))
            .Order(StringComparer.Ordinal)];
    }

    // Every migration of the Access context, in order, read from its assembly and not typed here.
    private static List<string> AccessMigrations()
    {
        using var context = DesignContexts.Create<AccessDbContext>();
        return [.. context.Database.GetMigrations().Order(StringComparer.Ordinal)];
    }

    // Breaks when a fresh database is not migrated to the catalog schema (the five catalog tables and one history row),
    // when the run does not exit 0, or when anything is created in public.
    [Fact]
    public async Task FreshDatabaseGetsTheCatalogSchemaAndNothingInPublic()
    {
        var database = await postgres.CreateDatabaseAsync();

        var run = await MigratorRun.ExecuteAsync(database);

        Assert.True(run.ExitCode == 0, run.Report);
        Assert.Equal(CatalogTables, await Db.TablesOfAsync(database, "catalog"));
        Assert.Single(await Db.ColumnAsync(database, HistoryOfCatalog));
        Assert.Empty(await Db.TablesOfAsync(database, "public"));
    }

    // Breaks when a second run fails, applies anything again, changes the history, or does not say that it applied nothing.
    [Fact]
    public async Task SecondRunExitsZeroChangesNothingAndReportsZeroApplied()
    {
        var database = await postgres.CreateDatabaseAsync();
        var tenant = Guid.NewGuid();
        Assert.Equal(0, (await MigratorRun.ExecuteAsync(database)).ExitCode);
        await CatalogSeed.AddTenantAsync(database, tenant);
        var first = await MigratorRun.ExecuteAsync(database);
        Assert.True(first.ExitCode == 0, first.Report);
        var catalogHistory = await Db.ColumnAsync(database, HistoryOfCatalog);
        var tenantHistory = await Db.ColumnAsync(
            database, $"SELECT migration_id FROM \"{TenantSchemaName.For(tenant)}\".__ef_migrations_access ORDER BY migration_id");

        var second = await MigratorRun.ExecuteAsync(database);

        Assert.True(second.ExitCode == 0, second.Report);
        Assert.Equal(catalogHistory, await Db.ColumnAsync(database, HistoryOfCatalog));
        Assert.Equal(
            tenantHistory,
            await Db.ColumnAsync(database, $"SELECT migration_id FROM \"{TenantSchemaName.For(tenant)}\".__ef_migrations_access ORDER BY migration_id"));
        Assert.Matches(ZeroApplied(), second.Report);
        Assert.DoesNotMatch(NonZeroApplied(), second.Report);
        Assert.Matches(NonZeroApplied(), first.Report);
    }

    // Breaks when a tenant of the catalog is not migrated to its own schema with the Access tables and history, when a catalog
    // table lands in a tenant schema, when an Access table lands in the catalog, or when the report omits a tenant.
    [Fact]
    public async Task EveryTenantOfTheCatalogGetsItsOwnSchemaWithAccessTablesAndNoCatalogTables()
    {
        var database = await postgres.CreateDatabaseAsync();
        Assert.Equal(0, (await MigratorRun.ExecuteAsync(database)).ExitCode);
        var tenants = new[] { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var tenant in tenants)
        {
            await CatalogSeed.AddTenantAsync(database, tenant);
        }

        var run = await MigratorRun.ExecuteAsync(database);

        Assert.True(run.ExitCode == 0, run.Report);
        var expected = AccessTenantTables();
        Assert.Contains("users", expected);
        foreach (var tenant in tenants)
        {
            var tables = await Db.TablesOfAsync(database, TenantSchemaName.For(tenant));
            Assert.Equal(expected, tables);
            Assert.Empty(tables.Intersect(CatalogTableNames));
            Assert.Contains(tenant.ToString("D"), run.Report, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(CatalogTables, await Db.TablesOfAsync(database, "catalog"));
        Assert.Empty(await Db.TablesOfAsync(database, "public"));
    }

    // Breaks when one tenant's failure stops the others, hides the failure behind exit 0, or leaves the failed tenant
    // unnamed in the report.
    [Fact]
    public async Task ATenantWithAConflictingTableFailsWithExitOneAndTheOtherTenantIsMigrated()
    {
        var database = await postgres.CreateDatabaseAsync();
        Assert.Equal(0, (await MigratorRun.ExecuteAsync(database)).ExitCode);
        var good = Guid.NewGuid();
        var bad = Guid.NewGuid();
        await CatalogSeed.AddTenantAsync(database, good);
        await CatalogSeed.AddTenantAsync(database, bad);
        await Db.ExecuteAsync(
            database, $"CREATE SCHEMA \"{TenantSchemaName.For(bad)}\"; CREATE TABLE \"{TenantSchemaName.For(bad)}\".users (id integer)");

        var run = await MigratorRun.ExecuteAsync(database);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(AccessTenantTables(), await Db.TablesOfAsync(database, TenantSchemaName.For(good)));
        var lines = run.Report.Split('\n');
        var badLine = Assert.Single(lines, l => l.Contains(bad.ToString("D"), StringComparison.OrdinalIgnoreCase));
        Assert.Matches(FailureWord(), badLine);
        var goodLine = Assert.Single(lines, l => l.Contains(good.ToString("D"), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotMatch(FailureWord(), goodLine);
    }

    // Breaks when two migrators that start together on one fresh database do not serialise (a failure from the race, a
    // migration applied twice or missing) or when either exits non-zero.
    [Fact]
    public async Task TwoConcurrentRunsOnAFreshDatabaseBothSucceedAndEveryMigrationIsRecordedOnce()
    {
        var database = await postgres.CreateDatabaseAsync();

        var runs = await Task.WhenAll(MigratorRun.ExecuteAsync(database), MigratorRun.ExecuteAsync(database));

        Assert.All(runs, r => Assert.True(r.ExitCode == 0, r.Report));
        var history = await Db.ColumnAsync(database, "SELECT migration_id FROM catalog.__ef_migrations_catalog");
        Assert.NotEmpty(history);
        Assert.Equal(history.Count, history.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(CatalogTables, await Db.TablesOfAsync(database, "catalog"));
    }

    // Breaks when concurrent runs with tenants present apply a tenant's migrations twice or fail on the lock.
    [Fact]
    public async Task TwoConcurrentRunsWithATenantRecordEachTenantMigrationOnce()
    {
        var database = await postgres.CreateDatabaseAsync();
        Assert.Equal(0, (await MigratorRun.ExecuteAsync(database)).ExitCode);
        var tenant = Guid.NewGuid();
        await CatalogSeed.AddTenantAsync(database, tenant);

        var runs = await Task.WhenAll(MigratorRun.ExecuteAsync(database), MigratorRun.ExecuteAsync(database));

        Assert.All(runs, r => Assert.True(r.ExitCode == 0, r.Report));
        var history = await Db.ColumnAsync(
            database, $"SELECT migration_id FROM \"{TenantSchemaName.For(tenant)}\".__ef_migrations_access");
        Assert.NotEmpty(history);
        Assert.Equal(history.Count, history.Distinct(StringComparer.Ordinal).Count());
    }

    // Breaks when the run does not wait for the global lock (it would finish or create the catalog while the test holds
    // the lock), or does not go on once the lock is released.
    [Fact]
    public async Task WhileAnotherSessionHoldsTheGlobalLockTheRunWaitsAndAfterReleaseItSucceeds()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var holder = await Db.OpenAsync(database);
        await using (var take = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_lock(pg_catalog.hashtextextended($1, 0))", holder))
        {
            take.Parameters.AddWithValue(DatabaseMigrator.GlobalLockKey);
            await take.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var run = MigratorRun.ExecuteAsync(database);
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.False(run.IsCompleted, "The run finished while the global lock was held.");
        Assert.False(await Db.SchemaExistsAsync(database, "catalog"), "The run created the catalog while the global lock was held.");

        await using (var release = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_unlock(pg_catalog.hashtextextended($1, 0))", holder))
        {
            release.Parameters.AddWithValue(DatabaseMigrator.GlobalLockKey);
            await release.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var finished = await run.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.True(finished.ExitCode == 0, finished.Report);
        Assert.Equal(CatalogTables, await Db.TablesOfAsync(database, "catalog"));
    }

    // Breaks when a missing connection string is not refused with exit 2 and a message naming the environment variable form
    // of the setting.
    [Fact]
    public async Task MissingConnectionStringExitsTwoAndNamesTheSetting()
    {
        var run = await MigratorRun.ExecuteRawAsync();

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("ConnectionStrings__Database", run.Report, StringComparison.Ordinal);
    }

    // Breaks when a parallelism below one (zero, negative) is accepted or refused without naming the setting, or when the
    // lowest valid value is refused.
    [Theory]
    [InlineData("0", 2)]
    [InlineData("-1", 2)]
    [InlineData("1", 0)]
    public async Task ParallelismBelowOneExitsTwoAndNamesTheSettingAndOneIsAccepted(string value, int expectedExit)
    {
        var database = await postgres.CreateDatabaseAsync();

        var run = await MigratorRun.ExecuteAsync(database, $"--{MigratorRun.ParallelismKey}={value}");

        Assert.True(run.ExitCode == expectedExit, run.Report);
        if (expectedExit == 2)
        {
            Assert.Contains(MigratorRun.ParallelismKey, run.Report, StringComparison.Ordinal);
            Assert.False(await Db.SchemaExistsAsync(database, "catalog"), "Invalid settings must stop the run before any migration.");
        }
    }

    // Breaks when the report quotes the connection string or its password, on success, on invalid settings and when the
    // database cannot be reached.
    [Fact]
    public async Task TheReportNeverContainsTheDatabasePassword()
    {
        var good = await postgres.CreateDatabaseAsync();
        var unreachable = new NpgsqlConnectionStringBuilder(good) { Port = 1, Timeout = 2 }.ConnectionString;

        var success = await MigratorRun.ExecuteAsync(good);
        var invalidSetting = await MigratorRun.ExecuteAsync(good, $"--{MigratorRun.ParallelismKey}=0");
        var cannotConnect = await MigratorRun.ExecuteAsync(unreachable);

        Assert.Equal(0, success.ExitCode);
        Assert.Equal(2, invalidSetting.ExitCode);
        Assert.NotEqual(0, cannotConnect.ExitCode);
        foreach (var run in new[] { success, invalidSetting, cannotConnect })
        {
            Assert.DoesNotContain(PostgresFixture.Password, run.Report, StringComparison.Ordinal);
        }
    }

    [GeneratedRegex(@"(?<![\d.])0\s+migration(\(s\)|s)?\s+applied", RegexOptions.IgnoreCase)]
    private static partial Regex ZeroApplied();

    [GeneratedRegex(@"(?<![\d.])[1-9]\d*\s+migration(\(s\)|s)?\s+applied", RegexOptions.IgnoreCase)]
    private static partial Regex NonZeroApplied();

    [GeneratedRegex("fail|error", RegexOptions.IgnoreCase)]
    private static partial Regex FailureWord();
}
