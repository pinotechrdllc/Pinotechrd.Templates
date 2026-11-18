using Microsoft.Extensions.DependencyInjection;
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
            connection.ConnectionString, new NpgsqlDataSourceBuilder(connection.ConnectionString).Build(), ownsDataSource: true, register);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var database in _databases)
        {
            await AdminAsync($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
        }

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
