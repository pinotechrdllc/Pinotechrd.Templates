using MyApp.Api.Tests.Database;
using Npgsql;
using MyApp.BuildingBlocks.Infrastructure.Migrations;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.Migrator.Tests.Support;

namespace MyApp.Migrator.Tests;

// Failure paths of the migrator command: a bad catalog row, a stop signal, a pool too small, and the grants of a migrated
// tenant. Same real PostgreSQL server and black-box style as the rest of the class.
public partial class MigratorCommandTests
{
    private const int CancelledExit = 130;

    private const string AdvisoryLocksOfThisDatabase =
        "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' "
        + "AND database = (SELECT oid FROM pg_database WHERE datname = current_database())";

    // Breaks when a catalog row that is not a usable tenant (the empty GUID) stops the run before the valid tenant is
    // migrated, hides behind exit 0, or is not named as failed in the report.
    [Fact]
    public async Task AnEmptyGuidTenantRowFailsWithExitOneAndTheValidTenantIsFullyMigrated()
    {
        var database = await postgres.CreateDatabaseAsync();
        Assert.Equal(0, (await MigratorRun.ExecuteAsync(database)).ExitCode);
        var good = Guid.NewGuid();
        await CatalogSeed.AddTenantAsync(database, good);
        await CatalogSeed.AddTenantAsync(database, Guid.Empty);

        var run = await MigratorRun.ExecuteAsync(database);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(AccessTenantTables(), await Db.TablesOfAsync(database, TenantSchemaName.For(good)));
        Assert.Single(await Db.ColumnAsync(
            database, $"SELECT migration_id FROM \"{TenantSchemaName.For(good)}\".__ef_migrations_access"));
        var lines = run.Report.Split('\n');
        var badLine = Assert.Single(lines, l => l.Contains(Guid.Empty.ToString("D"), StringComparison.OrdinalIgnoreCase));
        Assert.Matches(FailureWord(), badLine);
        var goodLine = Assert.Single(lines, l => l.Contains(good.ToString("D"), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotMatch(FailureWord(), goodLine);
    }

    // Breaks when a stop signal while the run waits for the global lock escapes as an exception, gives another exit code,
    // is not reported as a cancellation, leaves an advisory lock behind, or leaves the next run unable to finish.
    [Fact]
    public async Task CancellingWhileWaitingForTheGlobalLockExitsOneThirtyReportsItLeavesNoLockAndARerunSucceeds()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using (var holder = await Db.OpenAsync(database))
        {
            await using (var take = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_lock(pg_catalog.hashtextextended($1, 0))", holder))
            {
                take.Parameters.AddWithValue(DatabaseMigrator.GlobalLockKey);
                await take.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            using var report = new StringWriter();
            var running = MigratorHost.RunAsync([$"--{MigratorRun.ConnectionKey}={database}"], report, cancel.Token);
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            Assert.False(running.IsCompleted, "The run finished while the global lock was held.");

            await cancel.CancelAsync();
            var exit = await running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(CancelledExit, exit);
            Assert.Contains("cancel", report.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        // The holder is gone: any advisory lock left in this database belongs to the cancelled run.
        Assert.Equal(0L, await Db.CountAsync(database, AdvisoryLocksOfThisDatabase));
        var again = await MigratorRun.ExecuteAsync(database);
        Assert.True(again.ExitCode == 0, again.Report);
    }

    // Breaks when a token that is already cancelled starts the run, throws, or gives an exit other than 130.
    [Fact]
    public async Task AnAlreadyCancelledRunExitsOneThirtyWithoutMigratingAnything()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using var report = new StringWriter();

        var exit = await MigratorHost.RunAsync([$"--{MigratorRun.ConnectionKey}={database}"], report, cancelled.Token);

        Assert.Equal(CancelledExit, exit);
        Assert.Contains("cancel", report.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(await Db.SchemaExistsAsync(database, "catalog"));
    }

    // Breaks when a pool of two connections (too small for the global lock plus a tenant) is accepted, is refused with another
    // exit code or without naming the pool setting, or lets the run migrate something before refusing.
    [Fact]
    public async Task AMaximumPoolSizeOfTwoExitsTwoNamesThePoolSettingAndMigratesNothing()
    {
        var database = await postgres.CreateDatabaseAsync();
        var tooSmall = new NpgsqlConnectionStringBuilder(database) { MaxPoolSize = 2 }.ConnectionString;

        var run = await MigratorRun.ExecuteAsync(tooSmall);

        Assert.True(run.ExitCode == 2, run.Report);
        Assert.Contains("Pool Size", run.Report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PostgresFixture.Password, run.Report, StringComparison.Ordinal);
        Assert.False(await Db.SchemaExistsAsync(database, "catalog"), "A refused pool must stop the run before any migration.");
    }

    // Breaks when role_permissions.role_id or user_permissions.user_id has no foreign key to roles or users, when deleting a
    // role or a user leaves its grants, or when the same grant can be stored twice.
    [Fact]
    public async Task RoleAndUserPermissionsReferenceTheirOwnerCascadeOnDeleteAndRejectDuplicateGrants()
    {
        var (database, tenant) = await MigratedTenantAsync();
        var schema = TenantSchemaName.For(tenant);
        var permission = await NewPermissionAsync(database, schema);

        Assert.Equal(
            ["roles"],
            await ForeignKeyTargetsAsync(database, schema, "role_permissions", "role_id"));
        Assert.Equal(
            ["users"],
            await ForeignKeyTargetsAsync(database, schema, "user_permissions", "user_id"));

        var role = Guid.NewGuid();
        var user = Guid.NewGuid();
        await SchemaSeed.InsertAsync(database, schema, "roles", new() { ["id"] = role });
        await SchemaSeed.InsertAsync(database, schema, "users", new() { ["id"] = user });
        await SchemaSeed.InsertAsync(database, schema, "role_permissions", new() { ["role_id"] = role, ["permission_id"] = permission });
        await SchemaSeed.InsertAsync(database, schema, "user_permissions", new() { ["user_id"] = user, ["permission_id"] = permission });

        // A grant to an owner that does not exist is refused (foreign key violation).
        var ghostRole = await Assert.ThrowsAsync<PostgresException>(() => SchemaSeed.InsertAsync(
            database, schema, "role_permissions", new() { ["role_id"] = Guid.NewGuid(), ["permission_id"] = permission }));
        Assert.Equal("23503", ghostRole.SqlState);
        var ghostUser = await Assert.ThrowsAsync<PostgresException>(() => SchemaSeed.InsertAsync(
            database, schema, "user_permissions", new() { ["user_id"] = Guid.NewGuid(), ["permission_id"] = permission }));
        Assert.Equal("23503", ghostUser.SqlState);

        // The same grant twice is refused (unique violation).
        var duplicateRole = await Assert.ThrowsAsync<PostgresException>(() => SchemaSeed.InsertAsync(
            database, schema, "role_permissions", new() { ["role_id"] = role, ["permission_id"] = permission }));
        Assert.Equal("23505", duplicateRole.SqlState);
        var duplicateUser = await Assert.ThrowsAsync<PostgresException>(() => SchemaSeed.InsertAsync(
            database, schema, "user_permissions", new() { ["user_id"] = user, ["permission_id"] = permission }));
        Assert.Equal("23505", duplicateUser.SqlState);

        // Deleting the owner removes its grants and leaves the permission.
        await Db.ExecuteAsync(database, $"DELETE FROM \"{schema}\".roles WHERE id = '{role}'");
        await Db.ExecuteAsync(database, $"DELETE FROM \"{schema}\".users WHERE id = '{user}'");
        Assert.Equal(0L, await Db.CountAsync(database, $"SELECT count(*) FROM \"{schema}\".role_permissions WHERE role_id = '{role}'"));
        Assert.Equal(0L, await Db.CountAsync(database, $"SELECT count(*) FROM \"{schema}\".user_permissions WHERE user_id = '{user}'"));
        Assert.Equal(1L, await Db.CountAsync(database, $"SELECT count(*) FROM \"{schema}\".security_permissions WHERE id = '{permission}'"));
    }

    // Breaks when a foreign key of the migrated tenant schema points at a table of another schema (the catalog, another
    // tenant): a tenant must be able to be dropped and moved with its schema alone.
    [Fact]
    public async Task NoForeignKeyOfAMigratedTenantSchemaTargetsAnotherSchema()
    {
        var (database, tenant) = await MigratedTenantAsync();
        var schema = TenantSchemaName.For(tenant);

        var foreign = await Db.ColumnAsync(
            database,
            "SELECT c.conname || ' -> ' || tn.nspname || '.' || t.relname "
            + "FROM pg_constraint c JOIN pg_class s ON s.oid = c.conrelid JOIN pg_namespace sn ON sn.oid = s.relnamespace "
            + "JOIN pg_class t ON t.oid = c.confrelid JOIN pg_namespace tn ON tn.oid = t.relnamespace "
            + $"WHERE c.contype = 'f' AND sn.nspname = '{schema}' AND tn.nspname <> '{schema}'");

        Assert.Empty(foreign);
        Assert.NotEmpty(await Db.ColumnAsync(
            database,
            "SELECT 1 FROM pg_constraint c JOIN pg_class s ON s.oid = c.conrelid JOIN pg_namespace sn ON sn.oid = s.relnamespace "
            + $"WHERE c.contype = 'f' AND sn.nspname = '{schema}'"));
    }

    private async Task<(string Database, Guid Tenant)> MigratedTenantAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        Assert.Equal(0, (await MigratorRun.ExecuteAsync(database)).ExitCode);
        var tenant = Guid.NewGuid();
        await CatalogSeed.AddTenantAsync(database, tenant);
        var run = await MigratorRun.ExecuteAsync(database);
        Assert.True(run.ExitCode == 0, run.Report);
        return (database, tenant);
    }

    private static async Task<Guid> NewPermissionAsync(string database, string schema)
    {
        var module = Guid.NewGuid();
        var permission = Guid.NewGuid();
        await SchemaSeed.InsertAsync(database, schema, "security_modules", new() { ["id"] = module });
        await SchemaSeed.InsertAsync(database, schema, "security_permissions", new() { ["id"] = permission, ["module_id"] = module });
        return permission;
    }

    // The tables the foreign keys on one column point at, as text.
    private static Task<List<string>> ForeignKeyTargetsAsync(string database, string schema, string table, string column) =>
        Db.ColumnAsync(
            database,
            "SELECT DISTINCT t.relname FROM pg_constraint c JOIN pg_class s ON s.oid = c.conrelid "
            + "JOIN pg_namespace sn ON sn.oid = s.relnamespace JOIN pg_class t ON t.oid = c.confrelid "
            + "JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = ANY (c.conkey) "
            + $"WHERE c.contype = 'f' AND sn.nspname = '{schema}' AND s.relname = '{table}' AND a.attname = '{column}' "
            + "AND c.confdeltype = 'c' AND t.relnamespace = s.relnamespace");
}
