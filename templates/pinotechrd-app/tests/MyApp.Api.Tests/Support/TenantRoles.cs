using System.Collections.Concurrent;
using System.Globalization;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Npgsql;

namespace MyApp.Api.Tests.Support;

/// <summary>
/// The names of the tenant roles of a database, which carry the database's oid (roles live in the whole cluster). The oid
/// is read from the server through any login that can connect, and kept: a test database is never recreated under its name.
/// </summary>
internal static class TenantRoles
{
    private static readonly ConcurrentDictionary<string, uint> Oids = new(StringComparer.Ordinal);

    public static uint OidOf(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        var key = builder.Host + ":" + builder.Port.ToString(CultureInfo.InvariantCulture) + "/" + builder.Database;
        return Oids.GetOrAdd(key, _ =>
        {
            using var connection = new NpgsqlConnection(builder.ConnectionString);
            connection.Open();
            using var command = new NpgsqlCommand("SELECT oid::int8 FROM pg_database WHERE datname = current_database()", connection);
            return (uint)(long)command.ExecuteScalar()!;
        });
    }

    /// <summary>The role of <paramref name="tenant"/> in the database <paramref name="connectionString"/> names.</summary>
    public static string For(string connectionString, Guid tenant) => TenantRoleName.For(OidOf(connectionString), tenant);

    /// <summary>The shared role of the database <paramref name="connectionString"/> names.</summary>
    public static string SharedOf(string connectionString) => TenantRoleName.SharedFor(OidOf(connectionString));
}
