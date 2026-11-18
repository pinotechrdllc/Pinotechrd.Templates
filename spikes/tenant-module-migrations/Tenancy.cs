using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace TenantModuleMigrations;

/// <summary>How the tenant schema reaches PostgreSQL at runtime. Migrations and models never contain it.</summary>
public enum SchemaMode
{
    /// <summary>"Search Path=tenant_x" in the connection string. One Npgsql pool per tenant.</summary>
    ConnectionString,

    /// <summary>One shared connection string; an interceptor runs SET search_path on every connection open.</summary>
    Interceptor,
}

public static partial class Tenancy
{
    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$")]
    private static partial Regex SchemaName();

    public static string Validate(string schema) =>
        SchemaName().IsMatch(schema) ? schema : throw new ArgumentException($"Invalid schema name '{schema}'.");

    /// <summary>Builds the options for a module context bound to one tenant schema.</summary>
    public static DbContextOptions<TContext> Options<TContext>(
        string baseConnectionString, string schema, string historyTable, SchemaMode mode)
        where TContext : DbContext
    {
        Validate(schema);
        var builder = new DbContextOptionsBuilder<TContext>();
        if (mode == SchemaMode.ConnectionString)
        {
            // Only the tenant schema is on the path: if it is missing, DDL fails instead of landing in public.
            var cs = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema }.ConnectionString;
            builder.UseNpgsql(cs, o => o.MigrationsHistoryTable(historyTable));
        }
        else
        {
            builder.UseNpgsql(baseConnectionString, o => o.MigrationsHistoryTable(historyTable))
                .AddInterceptors(new SearchPathInterceptor(schema));
        }

        return builder.UseSnakeCaseNamingConvention().Options;
    }
}

/// <summary>Sets the search_path right after the connection opens, before EF sends anything.</summary>
public sealed class SearchPathInterceptor(string schema) : DbConnectionInterceptor
{
    private readonly string _sql = $"SET search_path TO \"{Tenancy.Validate(schema)}\"";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = _sql;
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = _sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
