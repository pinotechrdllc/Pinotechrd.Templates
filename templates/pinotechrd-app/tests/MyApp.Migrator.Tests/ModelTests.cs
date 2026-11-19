using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Infrastructure.DataProtection;
using MyApp.Migrator.Tests.Support;
using MyApp.Modules.Access.Infrastructure.Persistence;
using MyApp.Modules.Access.Infrastructure.Persistence.Catalog;

namespace MyApp.Migrator.Tests;

// The committed migrations against the models: no model change is left without a migration, and the catalog the migrator
// creates matches the catalog tables the Access context maps (and leaves out of its own migrations).
[Collection(PostgresContainer.Name)]
public class ModelTests(PostgresFixture postgres)
{
    // Breaks when the Access model changes (a property, an index, a table) and no migration of the context follows.
    [Fact]
    public void AccessContextHasNoPendingModelChanges()
    {
        using var context = DesignContexts.Create<AccessDbContext>();

        Assert.False(context.Database.HasPendingModelChanges());
    }

    // Breaks when the catalog model changes and no catalog migration follows.
    [Fact]
    public void CatalogContextHasNoPendingModelChanges()
    {
        using var context = DesignContexts.Create<CatalogDbContext>();

        Assert.False(context.Database.HasPendingModelChanges());
    }

    // Breaks when the Data Protection key model changes and no migration of the data_protection schema follows.
    [Fact]
    public void DataProtectionKeysContextHasNoPendingModelChanges()
    {
        using var context = DesignContexts.Create<DataProtectionKeysDbContext>();

        Assert.False(context.Database.HasPendingModelChanges());
    }

    // Breaks when a column of a catalog table that the Access context maps (and excludes from its migrations) is missing from
    // the catalog the migrator creates, or has another store type or nullability: the Access queries would fail at run time.
    [Fact]
    public async Task EveryColumnOfTheCatalogTablesTheAccessContextMapsExistsInTheMigratedCatalogWithTheSameTypeAndNullability()
    {
        var database = await postgres.CreateDatabaseAsync();
        var run = await MigratorRun.ExecuteAsync(database);
        Assert.True(run.ExitCode == 0, run.Report);
        using var context = DesignContexts.Create<AccessDbContext>();
        var excluded = context.GetService<IDesignTimeModel>().Model.GetRelationalModel().Tables
            .Where(t => t.IsExcludedFromMigrations)
            .ToList();
        Assert.True(excluded.Count >= 5, $"Expected the catalog tables in the Access model, found {excluded.Count}.");
        var checkedColumns = 0;

        foreach (var table in excluded)
        {
            Assert.Equal(AccessDbContext.CatalogSchema, table.Schema);
            var actual = await ColumnsOfAsync(database, table.Name);
            foreach (var column in table.Columns)
            {
                var found = Assert.Contains(column.Name, actual);
                Assert.True(found.StoreType == column.StoreType, $"catalog.{table.Name}.{column.Name}: store type {found.StoreType}, the Access model expects {column.StoreType}.");
                Assert.True(found.IsNullable == column.IsNullable, $"catalog.{table.Name}.{column.Name}: nullable {found.IsNullable}, the Access model expects {column.IsNullable}.");
                checkedColumns++;
            }
        }

        Assert.True(checkedColumns > 20, $"Only {checkedColumns} columns were compared.");
    }

    private sealed record Column(string StoreType, bool IsNullable);

    private static async Task<Dictionary<string, Column>> ColumnsOfAsync(string database, string table)
    {
        await using var connection = await Db.OpenAsync(database);
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT a.attname, pg_catalog.format_type(a.atttypid, a.atttypmod), NOT a.attnotnull "
            + "FROM pg_catalog.pg_attribute a JOIN pg_catalog.pg_class c ON c.oid = a.attrelid "
            + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
            + "WHERE n.nspname = 'catalog' AND c.relname = $1 AND a.attnum > 0 AND NOT a.attisdropped",
            connection);
        command.Parameters.AddWithValue(table);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var columns = new Dictionary<string, Column>(StringComparer.Ordinal);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            columns[reader.GetString(0)] = new Column(reader.GetString(1), reader.GetBoolean(2));
        }

        return columns;
    }
}
