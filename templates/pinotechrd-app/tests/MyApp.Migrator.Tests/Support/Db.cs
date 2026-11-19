using System.Globalization;
using Npgsql;

namespace MyApp.Migrator.Tests.Support;

/// <summary>Plain SQL helpers on a connection outside any pool under test.</summary>
internal static class Db
{
    public static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

#pragma warning disable CA2100 // The SQL is written by the tests; names in it are GUID-based or constants.
    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public static async Task<List<string>> ColumnAsync(string connectionString, string sql)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);
        }

        return rows;
    }

    public static async Task<long> CountAsync(string connectionString, string sql) =>
        long.Parse((await ColumnAsync(connectionString, sql))[0], CultureInfo.InvariantCulture);
#pragma warning restore CA2100

    /// <summary>The tables of one schema, ordered by name.</summary>
    public static Task<List<string>> TablesOfAsync(string connectionString, string schema) =>
        ColumnAsync(
            connectionString,
            $"SELECT table_name FROM information_schema.tables WHERE table_schema = '{schema}' ORDER BY table_name");

    public static async Task<bool> SchemaExistsAsync(string connectionString, string schema) =>
        await CountAsync(connectionString, $"SELECT count(*) FROM pg_namespace WHERE nspname = '{schema}'") > 0;
}
