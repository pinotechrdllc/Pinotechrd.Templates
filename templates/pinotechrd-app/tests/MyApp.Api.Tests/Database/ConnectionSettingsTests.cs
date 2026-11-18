using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Support;
using MyApp.Infrastructure.Database;
using Npgsql;

namespace MyApp.Api.Tests.Database;

// Criteria 20 and 21: what the application sets on the connection, read from the registered data source's own
// (sanitized) connection string. No database is needed to resolve it, so these run without Docker.
public class ConnectionSettingsTests
{
    private const string Password = "Persisted-Pw-6e21";

    private const string Base = "Host=127.0.0.1;Port=1;Database=settings_db;Username=settings_user;Password=" + Password;

    private static NpgsqlConnectionStringBuilder SettingsOf(string connectionString)
    {
        using var factory = new ApiHostFactory().WithDatabase(connectionString);
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();
        Assert.DoesNotContain(Password, dataSource.ConnectionString, StringComparison.Ordinal);
        return new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
    }

    // Breaks when the application stops forcing Persist Security Info off: the data source would hand the password back.
    [Theory]
    [InlineData("Persist Security Info=true")]
    [InlineData("PersistSecurityInfo=true")]
    public void PersistSecurityInfoInTheConnectionStringDoesNotPutThePasswordInTheDataSourceConnectionString(string setting)
    {
        using var factory = new ApiHostFactory().WithDatabase($"{Base};{setting}");
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();

        Assert.DoesNotContain(Password, dataSource.ConnectionString, StringComparison.Ordinal);
    }

    // Breaks when the default maximum pool size is not 20, or the application overrides a value the connection string sets.
    [Theory]
    [InlineData("", 20)]
    [InlineData(";Maximum Pool Size=7", 7)]
    [InlineData(";MaxPoolSize=9", 9)]
    [InlineData(";maximum pool size=12", 12)]
    [InlineData(";Maximum Pool Size=100", 100)]
    public void MaximumPoolSizeIsTwentyUnlessTheConnectionStringSetsOne(string setting, int expected)
    {
        Assert.Equal(20, DatabaseRegistration.DefaultMaximumPoolSize);

        Assert.Equal(expected, SettingsOf(Base + setting).MaxPoolSize);
    }

    // Breaks when the default command timeout is not 30 seconds, or the application overrides a value the connection
    // string sets (zero, which means no limit, included).
    [Theory]
    [InlineData("", 30)]
    [InlineData(";Command Timeout=7", 7)]
    [InlineData(";CommandTimeout=9", 9)]
    [InlineData(";Command Timeout=0", 0)]
    public void CommandTimeoutIsThirtySecondsUnlessTheConnectionStringSetsOne(string setting, int expected)
    {
        Assert.Equal(30, DatabaseRegistration.DefaultCommandTimeoutSeconds);

        Assert.Equal(expected, SettingsOf(Base + setting).CommandTimeout);
    }
}

// Criterion 20 on an opened connection. Needs Docker.
[Collection(PostgresContainer.Name)]
public class OpenedConnectionSettingsTests(PostgresFixture postgres)
{
    // Breaks when an opened connection hands the password back through its ConnectionString.
    [Fact]
    public async Task PersistSecurityInfoDoesNotPutThePasswordInAnOpenedConnectionString()
    {
        var connection = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync()) { PersistSecurityInfo = true };
        using var factory = new ApiHostFactory().WithDatabase(connection.ConnectionString);
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();

        await using var opened = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(System.Data.ConnectionState.Open, opened.State);
        Assert.DoesNotContain(PostgresFixture.Password, opened.ConnectionString, StringComparison.Ordinal);
        Assert.DoesNotContain(PostgresFixture.Password, dataSource.ConnectionString, StringComparison.Ordinal);
        // Control: the same string opened directly does hand the password back, so the assertions above can fail.
        await using var raw = new NpgsqlConnection(connection.ConnectionString);
        await raw.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Contains(PostgresFixture.Password, raw.ConnectionString, StringComparison.Ordinal);
    }
}
