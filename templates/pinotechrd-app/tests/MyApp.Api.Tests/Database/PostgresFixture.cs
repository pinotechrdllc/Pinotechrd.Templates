using Npgsql;
using Testcontainers.PostgreSql;

namespace MyApp.Api.Tests.Database;

/// <summary>
/// One PostgreSQL container for every database test of the run, started once and disposed at the end (the
/// <see cref="PostgresContainer"/> shares it). A test that needs its own state asks for
/// <see cref="CreateDatabaseAsync"/>: a new database in the container, so tests never depend on each other or on their
/// order. When Docker is not available the start fails with a message that names Docker, so every database test fails
/// instead of being skipped.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>The image: the same minor version the README's <c>docker run</c> line uses.</summary>
    public const string Image = "postgres:17.7";

    public const string Username = "fixture_user_q7";

    public const string Password = "Fixture-Pw-7f3a9c";

    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(5);

    private readonly PostgreSqlContainer _container;

    public PostgresFixture()
        : this(null)
    {
    }

    private PostgresFixture(string? dockerEndpoint)
    {
        var builder = new PostgreSqlBuilder(Image).WithUsername(Username).WithPassword(Password);
        if (dockerEndpoint is not null)
        {
            builder = builder.WithDockerEndpoint(dockerEndpoint);
        }

        _container = builder.Build();
    }

    /// <summary>A fixture that looks for Docker at <paramref name="dockerEndpoint"/>, to prove how its absence is reported.</summary>
    public static PostgresFixture ForDockerEndpoint(string dockerEndpoint) => new(dockerEndpoint);

    public string Host => _container.Hostname;

    public int Port => _container.GetMappedPublicPort(PostgreSqlBuilder.PostgreSqlPort);

    public async ValueTask InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(StartTimeout);
        try
        {
            await _container.StartAsync(timeout.Token);
        }
#pragma warning disable CA1031 // Whatever stops the container from starting is reported the same way.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            throw new InvalidOperationException(
                "The database tests need Docker, and the PostgreSQL container could not be started. Start Docker "
                + $"(Docker Desktop or the Docker daemon) and run the tests again. Image: {Image}. Cause: {exception.Message}",
                exception);
        }
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>The connection string of <paramref name="database"/> as the fixture's user, to change before use.</summary>
    public NpgsqlConnectionStringBuilder Connection(string database) => new()
    {
        Host = Host,
        Port = Port,
        Username = Username,
        Password = Password,
        Database = database,
        Timeout = 10,
    };

    /// <summary>Creates an empty database that only the caller uses and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        var admin = Connection("postgres");
        admin.Pooling = false;
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The name is a GUID the method just made.
        command.CommandText = "CREATE DATABASE " + name;
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return Connection(name).ConnectionString;
    }

    /// <summary>The server's count of open connections whose application name is <paramref name="applicationName"/>.</summary>
    public async Task<long> CountConnectionsAsync(string applicationName)
    {
        var admin = Connection("postgres");
        admin.Pooling = false;
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE application_name = $1", connection);
        command.Parameters.AddWithValue(applicationName);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}

/// <summary>Puts every test class marked with <see cref="Name"/> in one collection, so they share one container.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresContainer : ICollectionFixture<PostgresFixture>
{
    public const string Name = "PostgreSQL";
}
