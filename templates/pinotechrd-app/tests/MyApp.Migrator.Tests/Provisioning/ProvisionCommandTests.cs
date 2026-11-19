using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.Migrator.Tests.Support;

namespace MyApp.Migrator.Tests.Provisioning;

// The `provision` verb of the migrator against a real PostgreSQL server: black box, through MigratorHost.RunAsync, reading
// the catalog, the schemas and the report.
[Collection(PostgresContainer.Name)]
public class ProvisionCommandTests(PostgresFixture postgres)
{
    private const string Slug = "acme";

    private const string Name = "Acme";

    // Breaks when a first provision does not exit 0 or leaves anything but one Active row with one attempt, a completion time,
    // no error, db_schema = TenantSchemaName.For(id) and a migrated Access schema.
    [Fact]
    public async Task ProvisioningANewTenantCreatesOneActiveRowAndAMigratedSchema()
    {
        var (database, subscription) = await ProvisionSupport.ArrangeAsync(postgres);

        var run = await ProvisionSupport.RunAsync(database, Slug, Name, subscription);

        Assert.True(run.ExitCode == 0, run.Report);
        var row = Assert.Single(await ProvisionSupport.RowsAsync(database, Slug));
        Assert.Equal(Name, row.Name);
        Assert.Equal(subscription, row.SubscriptionId);
        Assert.Equal(ProvisionSupport.Active, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.NotNull(row.CompletedAt);
        Assert.Null(row.Error);
        Assert.Equal(TenantSchemaName.For(row.Id), row.DbSchema);
        var tables = await Db.TablesOfAsync(database, TenantSchemaName.For(row.Id));
        Assert.Contains("users", tables);
        Assert.Contains("__ef_migrations_access", tables);
        Assert.NotEmpty(await ProvisionSupport.HistoryAsync(database, row.Id));
        Assert.Equal(await ProvisionSupport.ReferenceTablesAsync(database), tables);
    }

    // Breaks when a repeat of the same request fails, does not say it was already provisioned, or changes the row, the
    // attempts or the migration history.
    [Fact]
    public async Task ProvisioningTheSameRequestAgainExitsZeroSaysAlreadyProvisionedAndChangesNothing()
    {
        var (database, subscription) = await ProvisionSupport.ArrangeAsync(postgres);
        Assert.Equal(0, (await ProvisionSupport.RunAsync(database, Slug, Name, subscription)).ExitCode);
        var before = Assert.Single(await ProvisionSupport.RowsAsync(database));
        var historyBefore = await ProvisionSupport.HistoryAsync(database, before.Id);

        var again = await ProvisionSupport.RunAsync(database, Slug, Name, subscription);

        Assert.True(again.ExitCode == 0, again.Report);
        Assert.Contains("already provisioned", again.Report, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, Assert.Single(await ProvisionSupport.RowsAsync(database)));
        Assert.Equal(historyBefore, await ProvisionSupport.HistoryAsync(database, before.Id));
    }

    // Breaks when a slug that is taken by another name is provisioned over, or the existing row is touched.
    [Fact]
    public async Task TheSameSlugWithAnotherNameExitsOneAndLeavesTheRowUntouched()
    {
        var (database, subscription) = await ProvisionSupport.ArrangeAsync(postgres);
        Assert.Equal(0, (await ProvisionSupport.RunAsync(database, Slug, Name, subscription)).ExitCode);
        var before = Assert.Single(await ProvisionSupport.RowsAsync(database));

        var run = await ProvisionSupport.RunAsync(database, Slug, "Another name", subscription);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(before, Assert.Single(await ProvisionSupport.RowsAsync(database)));
    }

    // Breaks when a slug that is taken by another subscription is provisioned over, or the existing row is touched.
    [Fact]
    public async Task TheSameSlugWithAnotherSubscriptionExitsOneAndLeavesTheRowUntouched()
    {
        var (database, subscription) = await ProvisionSupport.ArrangeAsync(postgres);
        var other = await CatalogSeed.AddSubscriptionAsync(database);
        Assert.Equal(0, (await ProvisionSupport.RunAsync(database, Slug, Name, subscription)).ExitCode);
        var before = Assert.Single(await ProvisionSupport.RowsAsync(database));

        var run = await ProvisionSupport.RunAsync(database, Slug, Name, other);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(before, Assert.Single(await ProvisionSupport.RowsAsync(database)));
    }

    // Breaks when a subscription that is not in the catalog is accepted, or leaves a row or a schema behind.
    [Fact]
    public async Task AnUnknownSubscriptionExitsOneAndCreatesNoRowAndNoSchema()
    {
        var (database, _) = await ProvisionSupport.ArrangeAsync(postgres);

        var run = await ProvisionSupport.RunAsync(database, Slug, Name, Guid.NewGuid());

        Assert.Equal(1, run.ExitCode);
        Assert.Empty(await ProvisionSupport.RowsAsync(database));
        Assert.Equal(0L, await ProvisionSupport.TenantSchemaCountAsync(database));
    }

    // Breaks when a failed migration of the schema hides behind exit 0, loses the row, leaks the connection details into the
    // stored error, or when the retry does not finish the same row (attempts 2, Active, error cleared).
    [Fact]
    public async Task AFailedMigrationLeavesTheRowProvisioningWithASqlStateAndTheRetryCompletesIt()
    {
        var (database, subscription) = await ProvisionSupport.ArrangeAsync(postgres);
        var id = Guid.NewGuid();
        await CatalogSeed.AddTenantRowAsync(database, id, subscription, Slug, Name, ProvisionSupport.InProvisioning);
        var schema = TenantSchemaName.For(id);
        await Db.ExecuteAsync(database, $"CREATE SCHEMA \"{schema}\"; CREATE TABLE \"{schema}\".users (id integer)");

        var failed = await ProvisionSupport.RunAsync(database, Slug, Name, subscription);

        Assert.Equal(1, failed.ExitCode);
        var row = Assert.Single(await ProvisionSupport.RowsAsync(database));
        Assert.Equal(id, row.Id);
        Assert.Equal(ProvisionSupport.InProvisioning, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.NotNull(row.Error);
        Assert.Matches("^[0-9A-Z]{5}$", row.Error);
        foreach (var secret in new[] { PostgresFixture.Password, PostgresFixture.Username, "Host=", "Password" })
        {
            Assert.DoesNotContain(secret, row.Error, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, failed.Report, StringComparison.OrdinalIgnoreCase);
        }

        await Db.ExecuteAsync(database, $"DROP TABLE \"{schema}\".users");
        var retried = await ProvisionSupport.RunAsync(database, Slug, Name, subscription);

        Assert.True(retried.ExitCode == 0, retried.Report);
        var done = Assert.Single(await ProvisionSupport.RowsAsync(database));
        Assert.Equal(id, done.Id);
        Assert.Equal(ProvisionSupport.Active, done.Status);
        Assert.Equal(2, done.Attempts);
        Assert.Null(done.Error);
        Assert.NotNull(done.CompletedAt);
        Assert.Contains("users", await Db.TablesOfAsync(database, schema));
    }

    // Breaks when a row left in Provisioning by a crash (no schema yet) is not completed by the next provision.
    [Fact]
    public async Task ARowLeftProvisioningWithoutASchemaIsCompleted()
    {
        var (database, subscription) = await ProvisionSupport.ArrangeAsync(postgres);
        var id = Guid.NewGuid();
        await CatalogSeed.AddTenantRowAsync(database, id, subscription, Slug, Name, ProvisionSupport.InProvisioning);
        Assert.False(await Db.SchemaExistsAsync(database, TenantSchemaName.For(id)));

        var run = await ProvisionSupport.RunAsync(database, Slug, Name, subscription);

        Assert.True(run.ExitCode == 0, run.Report);
        var row = Assert.Single(await ProvisionSupport.RowsAsync(database));
        Assert.Equal(id, row.Id);
        Assert.Equal(ProvisionSupport.Active, row.Status);
        Assert.Contains("users", await Db.TablesOfAsync(database, TenantSchemaName.For(id)));
    }

    // Breaks when a row left Provisioning with a migrated schema is not marked Active, or the schema is migrated again.
    [Fact]
    public async Task AMigratedSchemaWithARowStillProvisioningIsMarkedActiveAndNothingIsApplied()
    {
        var (database, subscription) = await ProvisionSupport.ArrangeAsync(postgres);
        var id = Guid.NewGuid();
        await CatalogSeed.AddTenantRowAsync(database, id, subscription, Slug, Name, ProvisionSupport.InProvisioning);
        Assert.Equal(0, (await MigratorRun.ExecuteAsync(database)).ExitCode);
        var history = await ProvisionSupport.HistoryAsync(database, id);
        Assert.NotEmpty(history);

        var run = await ProvisionSupport.RunAsync(database, Slug, Name, subscription);

        Assert.True(run.ExitCode == 0, run.Report);
        var row = Assert.Single(await ProvisionSupport.RowsAsync(database));
        Assert.Equal(ProvisionSupport.Active, row.Status);
        Assert.Equal(history, await ProvisionSupport.HistoryAsync(database, id));
    }

    // Breaks when a suspended tenant is provisioned again, its status changes, or a schema is created for it.
    [Fact]
    public async Task ASuspendedTenantExitsOneKeepsItsStatusAndMigratesNothing()
    {
        var (database, subscription) = await ProvisionSupport.ArrangeAsync(postgres);
        var id = Guid.NewGuid();
        await CatalogSeed.AddTenantRowAsync(database, id, subscription, Slug, Name, ProvisionSupport.Suspended);
        var before = Assert.Single(await ProvisionSupport.RowsAsync(database));

        var run = await ProvisionSupport.RunAsync(database, Slug, Name, subscription);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(before, Assert.Single(await ProvisionSupport.RowsAsync(database)));
        Assert.False(await Db.SchemaExistsAsync(database, TenantSchemaName.For(id)));
    }

    // Breaks when provisioning a database whose catalog is not migrated creates anything or does not tell to run migrate first.
    [Fact]
    public async Task ACatalogThatIsNotMigratedExitsOneTellsToRunMigrateAndCreatesNothing()
    {
        var database = await postgres.CreateDatabaseAsync();

        var run = await ProvisionSupport.RunAsync(database, Slug, Name, Guid.NewGuid());

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("migrate", run.Report, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0L, await ProvisionSupport.TenantSchemaCountAsync(database));
        Assert.False(await Db.SchemaExistsAsync(database, "catalog"));
    }

    // Breaks when an unknown verb is run as a migration, or does not exit 2.
    [Fact]
    public async Task AnUnknownVerbExitsTwoAndMigratesNothing()
    {
        var database = await postgres.CreateDatabaseAsync();

        var run = await MigratorRun.ExecuteRawAsync("bogus", $"--{MigratorRun.ConnectionKey}={database}");

        Assert.Equal(2, run.ExitCode);
        Assert.False(await Db.SchemaExistsAsync(database, "catalog"));
    }

    // Breaks when a one-character slug, inner hyphens or the longest slug (63 characters) are refused.
    [Theory]
    [InlineData("a")]
    [InlineData("a1-b2")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyza")]
    public async Task ValidBoundarySlugsAreProvisioned(string slug)
    {
        var (database, subscription) = await ProvisionSupport.ArrangeAsync(postgres);

        var run = await ProvisionSupport.RunAsync(database, slug, Name, subscription);

        Assert.True(run.ExitCode == 0, run.Report);
        Assert.Equal(ProvisionSupport.Active, Assert.Single(await ProvisionSupport.RowsAsync(database, slug)).Status);
    }

    // Breaks when a name of the longest length (200) is refused.
    [Fact]
    public async Task ANameOfTwoHundredCharactersIsProvisioned()
    {
        var (database, subscription) = await ProvisionSupport.ArrangeAsync(postgres);

        var run = await ProvisionSupport.RunAsync(database, Slug, new string('n', 200), subscription);

        Assert.True(run.ExitCode == 0, run.Report);
    }
}
