using System.Diagnostics;
using System.Net;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Web;
using MyApp.BuildingBlocks.Infrastructure.Database;
using MyApp.BuildingBlocks.Infrastructure.Migrations;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MyApp.Api.Tests.Database;

// The connection string is checked when the host starts, without a connection, and the check never
// repeats the value. None of these tests needs a database or Docker.
public class StartupTests
{
    private const string Password = "Pw-Secret-9f2c";
    private const string User = "svc_user_x";
    private const string Host = "db.internal.example";

    // Breaks when a missing, empty or blank connection string no longer stops the host with a validation error that
    // names ConnectionStrings:Database.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t \r\n")]
    public void UnsetConnectionStringStopsTheHostAndTheErrorNamesTheSetting(string? value)
    {
        using var factory = new ApiHostFactory();
        _ = value is null ? factory.WithoutDatabaseConnectionString() : factory.WithDatabase(value);

        StartupFailure.AssertOptionsInvalid(factory, "ConnectionStrings:Database");

        Assert.Equal("ConnectionStrings:Database", DatabaseRegistration.ConnectionStringKey);
    }

    // Breaks when an invalid value is accepted at start, when the error does not name the setting, or when the error
    // (message, inner exceptions, stack traces) or any log event quotes the value, its password, user or host.
    [Theory]
    [InlineData("this is not a connection string at all " + Password)]
    [InlineData("Database=app;Username=" + User + ";Password=" + Password)]
    [InlineData("Host=" + Host + ";Username=" + User + ";Password=" + Password + ";Not A Keyword=1")]
    [InlineData("Host=" + Host + ";Port=not-a-number;Username=" + User + ";Password=" + Password)]
    public void InvalidConnectionStringStopsTheHostAndNothingQuotesTheValue(string value)
    {
        using var factory = new ApiHostFactory().WithDatabase(value);
        var logs = factory.CaptureLogs();

        StartupFailure.AssertOptionsInvalidWithout(factory, DatabaseRegistration.ConnectionStringKey, value, Password, User, Host);

        foreach (var text in logs.Events.SelectMany(e => e.TextsOf()))
        {
            Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
            Assert.DoesNotContain(value, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Host, text, StringComparison.Ordinal);
        }
    }

    // Breaks when the error for a missing value quotes a connection string from somewhere else, or the logs do.
    [Fact]
    public void MissingConnectionStringErrorAndLogsContainNoConnectionStringText()
    {
        using var factory = new ApiHostFactory().WithoutDatabaseConnectionString();
        var logs = factory.CaptureLogs();

        StartupFailure.AssertOptionsInvalidWithout(
            factory, DatabaseRegistration.ConnectionStringKey, TestDatabase.PlaceholderPassword, "placeholder_user");

        Assert.DoesNotContain(logs.Events.SelectMany(e => e.TextsOf()), t => t.Contains(TestDatabase.PlaceholderPassword, StringComparison.Ordinal));
    }

    // Breaks when starting the host (or serving the version and the liveness probe) opens a database connection: the
    // connection string points to a server that accepts connections and never answers, so a connection attempt would
    // be visible at the socket, and the readiness probe at the end proves the detector sees one.
    [Fact]
    public async Task StartingTheHostAndServingVersionAndLiveOpensNoDatabaseConnection()
    {
        using var server = new SilentServer();
        using var factory = new ApiHostFactory().WithDatabase(server.ConnectionString("Silent-Pw-1"));
        var watch = Stopwatch.StartNew();
        using var client = factory.CreateClient();

        using var version = await client.GetAsync(SecurityRequests.Version, TestContext.Current.CancellationToken);
        using var live = await client.GetAsync(HealthRegistration.LivePath, TestContext.Current.CancellationToken);
        watch.Stop();

        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Start and two requests took {watch.Elapsed}.");
        Assert.False(server.HasConnectionAttempt, "The host connected to the database before anything needed it.");

        using var ready = await client.GetAsync(HealthRegistration.ReadyPath, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.True(server.HasConnectionAttempt, "The detector saw no connection attempt from the readiness probe.");
    }

    // Breaks when a valid connection string for a server that is down stops the start (the check must not connect).
    [Fact]
    public async Task ValidConnectionStringForAServerThatIsDownStartsTheHost()
    {
        using var factory = new ApiHostFactory().WithDatabase(TestDatabase.PlaceholderConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(SecurityRequests.Version, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

// The API never migrates: the migrator is a separate command. Needs Docker (the shared PostgreSQL container).
[Collection(PostgresContainer.Name)]
public class ApiDoesNotMigrateTests(PostgresFixture postgres)
{
    // Breaks when starting the API (and answering its readiness probe) creates the catalog schema, a tenant schema or any
    // table, or when the API registers a database migrator or a tenant migrator that something could resolve.
    [Fact]
    public async Task ApiStartedOnAFreshDatabaseCreatesNoSchemaAndRegistersNoMigrator()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        using var factory = new ApiHostFactory().WithDatabase(connectionString);
        using var client = factory.CreateClient();

        using var ready = await client.GetAsync(HealthRegistration.ReadyPath, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Null(factory.Services.GetService<IDatabaseMigrator>());
        Assert.Null(factory.Services.GetService<ITenantMigrator>());
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Null(scope.ServiceProvider.GetService<IDatabaseMigrator>());
        Assert.Null(scope.ServiceProvider.GetService<ITenantMigrator>());

        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var schemas = new NpgsqlCommand(
            "SELECT nspname FROM pg_namespace WHERE nspname = 'catalog' OR left(nspname, 7) = 'tenant_' OR nspname = 'extensions'", connection);
        await using (var reader = await schemas.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken), "The API created a schema.");
        }

        await using var tables = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema')", connection);
        Assert.Equal(0L, (long)(await tables.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }
}
