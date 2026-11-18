using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyApp.Api.Tests.Support;
using Npgsql;

namespace MyApp.Api.Tests.Database;

// The registration of the shared data source. It resolves the object without opening a connection, so
// no database or Docker is needed.
public class DataSourceRegistrationTests
{
    // Breaks when the data source is registered as scoped or transient (each scope would get its own pool).
    [Fact]
    public void TheSameDataSourceIsSharedByEveryScope()
    {
        using var factory = new ApiHostFactory();

        NpgsqlDataSource first;
        NpgsqlDataSource second;
        using (var scope = factory.Services.CreateScope())
        {
            first = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        }

        using (var scope = factory.Services.CreateScope())
        {
            second = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        }

        Assert.Same(first, second);
        Assert.Same(first, factory.Services.GetRequiredService<NpgsqlDataSource>());
    }

    // Breaks when the host does not dispose the data source (its pool would outlive the application).
    [Fact]
    public async Task DataSourceIsDisposedWhenTheHostIsDisposed()
    {
        var factory = new ApiHostFactory();
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();

        await factory.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        });
    }
}

// The data source against a real server. Needs Docker.
[Collection(PostgresContainer.Name)]
public class DataSourceBehaviorTests(PostgresFixture postgres)
{
    private static string ApiName => typeof(Program).Assembly.GetName().Name!;

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    // Breaks when the registered data source cannot run a query against the configured database.
    [Fact]
    public async Task QueryThroughTheRegisteredDataSourceReachesTheConfiguredDatabase()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        using var factory = new ApiHostFactory().WithDatabase(connectionString);
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();

        Assert.Equal(1, await ScalarAsync<int>(dataSource, "SELECT 1"));
        Assert.Equal(new NpgsqlConnectionStringBuilder(connectionString).Database, await ScalarAsync<string>(dataSource, "SELECT current_database()"));
    }

    // Breaks when the connection stops reporting the application name (the README documents the application name).
    [Fact]
    public async Task ConnectionReportsTheApplicationNameWhenTheConnectionStringHasNone()
    {
        using var factory = new ApiHostFactory().WithDatabase(await postgres.CreateDatabaseAsync());
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();

        var reported = await ScalarAsync<string>(dataSource, "SHOW application_name");

        Assert.Equal(factory.Services.GetRequiredService<IHostEnvironment>().ApplicationName, reported);
        Assert.Equal(ApiName, reported);
    }

    // Breaks when the application name overwrites one the connection string chose.
    [Fact]
    public async Task ApplicationNameOfTheConnectionStringIsKept()
    {
        var connection = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync()) { ApplicationName = "chosen-name" };
        using var factory = new ApiHostFactory().WithDatabase(connection.ConnectionString);
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();

        Assert.Equal("chosen-name", await ScalarAsync<string>(dataSource, "SHOW application_name"));
    }

    // Breaks when the application lets the server's error detail (row values) into exceptions, even when the connection
    // string asks for it. The control at the end shows that the same statement does carry the value with the setting
    // honored, so the assertion cannot pass because the violation never produced a detail.
    [Fact]
    public async Task ServerErrorDetailStaysOffEvenWhenTheConnectionStringAsksForIt()
    {
        const string RowValue = "row-secret-value-4417";
        var connection = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync()) { IncludeErrorDetail = true };
        using var factory = new ApiHostFactory().WithDatabase(connection.ConnectionString);
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await Execute(dataSource, "CREATE TABLE secrets (code text PRIMARY KEY)");
        await Execute(dataSource, $"INSERT INTO secrets VALUES ('{RowValue}')");

        var exception = await Assert.ThrowsAsync<PostgresException>(() => Execute(dataSource, $"INSERT INTO secrets VALUES ('{RowValue}')"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.DoesNotContain(RowValue, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(RowValue, exception.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(RowValue, exception.MessageText, StringComparison.Ordinal);

        await using var raw = NpgsqlDataSource.Create(connection.ConnectionString);
        var control = await Assert.ThrowsAsync<PostgresException>(() => Execute(raw, $"INSERT INTO secrets VALUES ('{RowValue}')"));
        Assert.Contains(RowValue, control.Detail, StringComparison.Ordinal);
    }

    private static async Task Execute(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
