using MyApp.Api.Tests.Database;
using Npgsql;

namespace MyApp.Migrator.Tests.Support;

/// <summary>
/// A database as production has it: owned by a migrator login that is not a superuser (CREATEROLE only), with a separate
/// application login. Dropped with its logins on dispose.
/// </summary>
internal sealed class SeparateLoginsDatabase : IAsyncDisposable
{
    public const string ApplicationRoleKey = "Migrator:ApplicationRole";

    private readonly PostgresFixture _postgres;
    private readonly string _name;

    private SeparateLoginsDatabase(PostgresFixture postgres, string name, string migrator, string application, string password, string migratorConnection, string applicationConnection)
    {
        _postgres = postgres;
        _name = name;
        Migrator = migrator;
        Application = application;
        Password = password;
        MigratorConnection = migratorConnection;
        ApplicationConnection = applicationConnection;
    }

    public string Migrator { get; }

    public string Application { get; }

    public string Password { get; }

    /// <summary>Connection string of the migrator login.</summary>
    public string MigratorConnection { get; }

    /// <summary>Connection string of the application login.</summary>
    public string ApplicationConnection { get; }

    /// <summary>The argument that names the application login for the migrator.</summary>
    public string RoleArgument => $"--{ApplicationRoleKey}={Application}";

    public static async Task<SeparateLoginsDatabase> CreateAsync(PostgresFixture postgres)
    {
        var suffix = Guid.NewGuid().ToString("N")[..16];
        var (migrator, application, password) = ("m_" + suffix, "a_" + suffix, "Pw-" + Guid.NewGuid().ToString("N"));
        var superuser = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync());
        var name = superuser.Database!;
        await AdminAsync(postgres, $"CREATE ROLE {migrator} LOGIN CREATEROLE PASSWORD '{password}'; CREATE ROLE {application} LOGIN PASSWORD '{password}'");
        await AdminAsync(postgres, $"ALTER DATABASE \"{name}\" OWNER TO {migrator}");
        var asMigrator = new NpgsqlConnectionStringBuilder(superuser.ConnectionString) { Username = migrator, Password = password, Pooling = false }.ConnectionString;
        var asApplication = new NpgsqlConnectionStringBuilder(superuser.ConnectionString) { Username = application, Password = password, Pooling = false }.ConnectionString;
        return new SeparateLoginsDatabase(postgres, name, migrator, application, password, asMigrator, asApplication);
    }

    public async ValueTask DisposeAsync()
    {
        await AdminAsync(_postgres, $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)");
        await AdminAsync(_postgres, $"DROP OWNED BY {Migrator}; DROP OWNED BY {Application}");
        await AdminAsync(_postgres, $"DROP ROLE IF EXISTS {Application}; DROP ROLE IF EXISTS {Migrator}");
    }

    private static async Task AdminAsync(PostgresFixture postgres, string sql)
    {
        var admin = postgres.Connection("postgres");
        admin.Pooling = false;
        await Db.ExecuteAsync(admin.ConnectionString, sql);
    }
}
