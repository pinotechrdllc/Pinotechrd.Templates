using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.Api.Tests.Database;
using Npgsql;

namespace MyApp.Api.Tests.Tenancy;

/// <summary>
/// A login role and a database that only one test uses, both dropped at the end, so a role-level setting or a revoked
/// privilege never leaks to another test. Roles live in the whole cluster, so the database goes first.
/// </summary>
internal sealed class DedicatedLogin : IAsyncDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly List<string> _databases = [];

    private DedicatedLogin(PostgresFixture postgres, string name, string password)
    {
        _postgres = postgres;
        Name = name;
        Password = password;
    }

    public string Name { get; }

    public string Password { get; }

    public static async Task<DedicatedLogin> CreateAsync(PostgresFixture postgres)
    {
        var name = "r_" + Guid.NewGuid().ToString("N")[..16];
        var login = new DedicatedLogin(postgres, name, "Pw-" + Guid.NewGuid().ToString("N"));
        await login.AdminAsync($"CREATE ROLE {name} LOGIN PASSWORD '{login.Password}'");
        return login;
    }

    /// <summary>Remembers a database to drop, before the role is dropped.</summary>
    public void Drops(string database) => _databases.Add(database);

    /// <summary>Starts a rig on <paramref name="database"/> that connects as this role.</summary>
    public TenantRig RigOn(string database, Action<IServiceCollection>? register = null, int maxPoolSize = 8)
    {
        var connection = _postgres.Connection(database);
        connection.Username = Name;
        connection.Password = Password;
        connection.MaxPoolSize = maxPoolSize;
        return TenantRig.StartOn(
            connection.ConnectionString, new NpgsqlDataSourceBuilder(connection.ConnectionString).Build(), ownsDataSource: true, register, allowPrivilegedLogin: false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var database in _databases)
        {
            await AdminAsync($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
        }

        // Memberships this login granted (as the creator of tenant roles) depend on it until its objects and grants are dropped.
        await AdminAsync($"DROP OWNED BY {Name}");
        await AdminAsync($"DROP ROLE IF EXISTS {Name}");
    }

    private async Task AdminAsync(string sql)
    {
        var admin = _postgres.Connection("postgres");
        admin.Pooling = false;
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
#pragma warning disable CA2100 // The SQL is written by the tests, with names made from GUIDs.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}

/// <summary>
/// The SQL that gives a hand-made tenant schema what the migrator gives it, for tests that do not run the migrator: the
/// tenant's NOLOGIN role (usage on the schema, data changes on its tables now and later), and the shared role that reads
/// the shared schemas that exist. Written for a superuser session (the fixture's user), which owns what it creates.
/// </summary>
internal static class TenantRoleSql
{
    public static string For(string connectionString, Guid tenantId, bool allowDdl = false)
    {
        var schema = "\"" + TenantSchemaName.For(tenantId) + "\"";
        var role = "\"" + TenantRoles.For(connectionString, tenantId) + "\"";
        var shared = "\"" + TenantRoles.SharedOf(connectionString) + "\"";
        return
            $"DO $$ BEGIN CREATE ROLE {shared} NOLOGIN; EXCEPTION WHEN duplicate_object OR unique_violation THEN NULL; END $$; "
            + $"DO $$ BEGIN CREATE ROLE {role} NOLOGIN; EXCEPTION WHEN duplicate_object OR unique_violation THEN NULL; END $$; "
            + $"GRANT {shared} TO {role}; "
            + $"DO $$ BEGIN IF EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'extensions') THEN GRANT USAGE ON SCHEMA extensions TO {shared}; END IF; "
            + $"IF EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'catalog') THEN GRANT USAGE ON SCHEMA catalog TO {shared}; "
            + $"GRANT SELECT ON ALL TABLES IN SCHEMA catalog TO {shared}; END IF; END $$; "
            + $"GRANT USAGE ON SCHEMA {schema} TO {role}; "
            + (allowDdl ? $"GRANT CREATE ON SCHEMA {schema} TO {role}; " : string.Empty)
            + $"ALTER DEFAULT PRIVILEGES IN SCHEMA {schema} GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {role}; "
            + $"ALTER DEFAULT PRIVILEGES IN SCHEMA {schema} GRANT USAGE, SELECT ON SEQUENCES TO {role}; "
            + $"GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {schema} TO {role}; "
            + $"GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {schema} TO {role}";
    }
}

/// <summary>Drops a database (and with it the role-level settings made in it) when the test ends.</summary>
internal sealed class DatabaseDrop(PostgresFixture postgres, string database) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        var admin = postgres.Connection("postgres");
        admin.Pooling = false;
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
#pragma warning disable CA2100 // The name is a GUID the fixture made.
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
