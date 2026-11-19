using System.Globalization;
using Npgsql;

namespace MyApp.Migrator.Tests.Support;

/// <summary>
/// Inserts one row into a table of any schema with plain SQL. Like <see cref="CatalogSeed"/>, the columns it must fill are
/// read from the database (every NOT NULL column without a default gets a value of its type), so a test names only the
/// columns it cares about.
/// </summary>
internal static class SchemaSeed
{
    public static async Task InsertAsync(string connectionString, string schema, string table, Dictionary<string, object> given)
    {
        await using var connection = await Db.OpenAsync(connectionString);
        var values = new Dictionary<string, object>(given);
        await using (var columns = new NpgsqlCommand(
            "SELECT column_name, data_type, character_maximum_length FROM information_schema.columns "
            + "WHERE table_schema = $1 AND table_name = $2 AND is_nullable = 'NO' AND column_default IS NULL "
            + "AND is_identity = 'NO' AND is_generated = 'NEVER'",
            connection))
        {
            columns.Parameters.AddWithValue(schema);
            columns.Parameters.AddWithValue(table);
            await using var reader = await columns.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                var name = reader.GetString(0);
                if (!values.ContainsKey(name))
                {
                    values[name] = DefaultFor(reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2));
                }
            }
        }

        var names = string.Join(", ", values.Keys.Select(k => "\"" + k + "\""));
        var parameters = string.Join(", ", values.Keys.Select((_, i) => "$" + (i + 1).ToString(CultureInfo.InvariantCulture)));
#pragma warning disable CA2100 // Names come from the database or the test; values are parameters.
        await using var insert = new NpgsqlCommand($"INSERT INTO \"{schema}\".\"{table}\" ({names}) VALUES ({parameters})", connection);
#pragma warning restore CA2100
        foreach (var value in values.Values)
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static object DefaultFor(string dataType, int? maxLength) => dataType switch
    {
        "uuid" => Guid.NewGuid(),
        "text" => "seed-" + Guid.NewGuid().ToString("N"),
        "character varying" => ("seed-" + Guid.NewGuid().ToString("N"))[..Math.Min(maxLength ?? 37, 37)],
        "integer" => 0,
        "smallint" => (short)0,
        "bigint" => 0L,
        "boolean" => false,
        "numeric" => 0m,
        "timestamp with time zone" => DateTime.UtcNow,
        "timestamp without time zone" => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified),
        _ => throw new InvalidOperationException($"The test seed has no value for a column of type '{dataType}'."),
    };
}
