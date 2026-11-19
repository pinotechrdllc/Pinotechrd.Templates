using MyApp.Api.Tests.Database;
using MyApp.Migrator.Tests.Support;
using Npgsql;

namespace MyApp.Migrator.Tests.Tenancy;

// The Data Protection key ring lives in the shared data_protection schema. The migrator creates it and grants the application
// login exactly what the API needs to keep keys: read the keys, add a key, nothing else (a stored key is never changed or
// removed by the application). Statements run as the application login on real PostgreSQL.
[Collection(PostgresContainer.Name)]
public class DataProtectionKeysAsApplicationLoginTests(PostgresFixture postgres)
{
    private const string Schema = "data_protection";

    private const string Keys = "data_protection.keys";

    private const string InsertKey = "INSERT INTO " + Keys + " (friendly_name, xml) VALUES ('key-1', '<key id=\"1\" />')";

    private static async Task<NpgsqlConnection> MigratedAsApplicationAsync(SeparateLoginsDatabase logins)
    {
        await BootstrapMigrateAsync(logins);
        var connection = new NpgsqlConnection(logins.ApplicationConnection);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static async Task BootstrapMigrateAsync(SeparateLoginsDatabase logins)
    {
        var run = await MigratorRun.ExecuteAsync(logins.MigratorConnection, logins.RoleArgument);
        Assert.True(run.ExitCode == 0, run.Report);
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The SQL is written by this test; the names in it are constants.
        command.CommandText = sql;
#pragma warning restore CA2100
        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> RefusalAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The SQL is written by this test; the names in it are constants.
        command.CommandText = sql;
#pragma warning restore CA2100
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        return error.SqlState;
    }

    // Breaks when the migrator does not create the data_protection schema with the keys table (id, friendly_name, xml), or its
    // own history table in that schema.
    [Fact]
    public async Task TheMigratorCreatesTheDataProtectionSchemaWithTheKeysTable()
    {
        var database = await postgres.CreateDatabaseAsync();

        var run = await MigratorRun.ExecuteAsync(database);

        Assert.True(run.ExitCode == 0, run.Report);
        Assert.True(await Db.SchemaExistsAsync(database, Schema));
        var tables = await Db.TablesOfAsync(database, Schema);
        Assert.Contains("keys", tables);
        Assert.Contains("__ef_migrations_data_protection", tables);
        Assert.Equal(
            ["friendly_name", "id", "signature", "signer", "xml"],
            await Db.ColumnAsync(
                database,
                $"SELECT column_name FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'keys' ORDER BY column_name"));
    }

    // Breaks when a second run of the migrator fails or duplicates the schema (every deployment runs it again).
    [Fact]
    public async Task RunningTheMigratorAgainLeavesTheKeysUntouched()
    {
        await using var logins = await SeparateLoginsDatabase.CreateAsync(postgres);
        await BootstrapMigrateAsync(logins);
        await Db.ExecuteAsync(logins.MigratorConnection, InsertKey);

        await BootstrapMigrateAsync(logins);

        Assert.Equal(1L, await Db.CountAsync(logins.MigratorConnection, $"SELECT count(*) FROM {Keys}"));
    }

    // Breaks when the application login lacks USAGE on the schema or SELECT on a column the key ring reads (the API reads every
    // key when it starts: a 42501 here is a host that does not start).
    [Fact]
    public async Task TheApplicationLoginReadsIdFriendlyNameAndXmlOfTheKeysAfterTheMigratorRan()
    {
        await using var logins = await SeparateLoginsDatabase.CreateAsync(postgres);
        await using var connection = await MigratedAsApplicationAsync(logins);
        await Db.ExecuteAsync(logins.MigratorConnection, InsertKey);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id, friendly_name, xml FROM {Keys}";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("key-1", reader.GetString(1));
        Assert.Equal("<key id=\"1\" />", reader.GetString(2));
    }

    // Breaks when the application login cannot add a key (the grant of INSERT on friendly_name and xml, or the right to use the
    // sequence of the id, is missing): the first start of a new deployment would fail to create its key.
    [Fact]
    public async Task TheApplicationLoginInsertsAKeyAndTheIdIsGenerated()
    {
        await using var logins = await SeparateLoginsDatabase.CreateAsync(postgres);
        await using var connection = await MigratedAsApplicationAsync(logins);

        var inserted = await ExecuteAsync(connection, InsertKey);
        _ = await ExecuteAsync(connection, InsertKey.Replace("key-1", "key-2", StringComparison.Ordinal));

        Assert.Equal(1, inserted);
        Assert.Equal(2L, await Db.CountAsync(logins.MigratorConnection, $"SELECT count(DISTINCT id) FROM {Keys}"));
    }

    // Breaks when the application login may change a stored key (a stolen login could replace a key by one it knows).
    [Fact]
    public async Task TheApplicationLoginCannotUpdateAKey()
    {
        await using var logins = await SeparateLoginsDatabase.CreateAsync(postgres);
        await using var connection = await MigratedAsApplicationAsync(logins);
        await Db.ExecuteAsync(logins.MigratorConnection, InsertKey);

        Assert.Equal("42501", await RefusalAsync(connection, $"UPDATE {Keys} SET xml = '<key id=\"changed\" />'"));
        Assert.Equal("42501", await RefusalAsync(connection, $"UPDATE {Keys} SET friendly_name = 'changed'"));
    }

    // Breaks when the application login may delete keys (every protected value would become unreadable).
    [Fact]
    public async Task TheApplicationLoginCannotDeleteOrTruncateKeys()
    {
        await using var logins = await SeparateLoginsDatabase.CreateAsync(postgres);
        await using var connection = await MigratedAsApplicationAsync(logins);
        await Db.ExecuteAsync(logins.MigratorConnection, InsertKey);

        Assert.Equal("42501", await RefusalAsync(connection, $"DELETE FROM {Keys}"));
        Assert.Equal("42501", await RefusalAsync(connection, $"TRUNCATE {Keys}"));
        Assert.Equal(1L, await Db.CountAsync(logins.MigratorConnection, $"SELECT count(*) FROM {Keys}"));
    }

    // Breaks when the application login may create objects in the schema or read its migration history.
    [Theory]
    [InlineData("CREATE TABLE " + Schema + ".extra (id int)")]
    [InlineData("SELECT * FROM " + Schema + ".__ef_migrations_data_protection")]
    public async Task TheApplicationLoginIsRefusedAnythingBeyondReadingAndAddingKeys(string sql)
    {
        await using var logins = await SeparateLoginsDatabase.CreateAsync(postgres);
        await using var connection = await MigratedAsApplicationAsync(logins);

        Assert.Equal("42501", await RefusalAsync(connection, sql));
    }

    // Breaks when the grants are only made for the catalog: the application login must still read the tenants of the catalog
    // after the key ring is added to the shared schemas (one grant step for every shared schema).
    [Fact]
    public async Task TheApplicationLoginStillReadsTheCatalogOnceTheKeyRingIsMigrated()
    {
        await using var logins = await SeparateLoginsDatabase.CreateAsync(postgres);
        await using var connection = await MigratedAsApplicationAsync(logins);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(id) FROM catalog.tenants";
        var count = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0L, count);
    }
}
