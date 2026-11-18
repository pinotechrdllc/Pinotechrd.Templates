using System.Data;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Infrastructure.Database;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;

namespace MyApp.Api.Tests.Tenancy;

// A new session's default search_path is what a connection falls back to after a rollback or a reset, so a tenant open
// refuses any default but PostgreSQL's. Needs Docker, except the reading of startup options.
public class StartupOptionsTests
{
    private static bool SetsSearchPath(string? options)
    {
        // ConnectionSafety is internal; PGOPTIONS reaches it through the environment, which a test cannot set without
        // changing the new connections of every test running in parallel. So the function that reads the options is called
        // with the text, and the reading of the environment variable itself is not exercised here.
        var type = typeof(DatabaseRegistration).Assembly.GetType(typeof(DatabaseRegistration).Namespace + ".ConnectionSafety", throwOnError: true)!;
        var method = type.GetMethod("SetsSearchPath", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        return (bool)method.Invoke(null, [options])!;
    }

    // Breaks when startup options that set search_path are read differently from the server: escapes (a backslash makes
    // the next character literal), a long option with dashes, any case, several options, quotes.
    [Theory]
    [InlineData("-c search_path=x", true)]
    [InlineData("-csearch_path=x", true)]
    [InlineData("-c search\\_path=x", true)]
    [InlineData("-c sea\\rch_path=x", true)]
    [InlineData("--search-path=x", true)]
    [InlineData("--search_path=x", true)]
    [InlineData("-c SeArCh_PaTh=x", true)]
    [InlineData("-c statement_timeout=5000 -c search_path=x", true)]
    [InlineData("\"-c search_path=x\"", true)]
    [InlineData("-c statement_timeout=5000", false)]
    [InlineData("-c application_name=search_path_app", false)]
    [InlineData("-c application_name=search_path_app -c work_mem=1MB", false)]
    [InlineData("-c work_mem=1MB -c search_path=x", true)]
    [InlineData("-c search_path=x -c work_mem=1MB", true)]
    [InlineData("-c statement_timeout=5 --search-path=x", true)]
    [InlineData("-csearch\\_path=x", true)]
    [InlineData("-c 'search_path=x'", true)]
    [InlineData("-c work_mem=1MB -c application_name=a", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void StartupOptionsThatSetTheSearchPathAreRecognizedTheWayTheServerReadsThem(string? options, bool expected)
    {
        Assert.Equal(expected, SetsSearchPath(options));
    }
}

[Collection(PostgresContainer.Name)]
public class SessionDefaultTests(PostgresFixture postgres)
{
    private const string SchemaSql = "SELECT current_schema() AS \"Value\"";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Breaks when Options that are not about search_path are refused (or ignored): a statement timeout in Options is legal
    // and must reach the server, with tenant isolation intact.
    [Fact]
    public async Task AStatementTimeoutInOptionsIsAcceptedAndWorks()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2, tune: c => c.Options = "-c statement_timeout=5000");
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();

        Assert.Equal(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
        Assert.Equal("5s", await context.Database.SqlQueryRaw<string>("SELECT current_setting('statement_timeout') AS \"Value\"").SingleAsync(Token));
    }

    private sealed class LogCollector : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Text)> _events = new();

        public IReadOnlyList<(LogLevel Level, string Text)> Events => [.. _events];

        public ILogger CreateLogger(string categoryName) => new Sink(_events);

        public void Dispose()
        {
        }

        private sealed class Sink(System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Text)> events) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                events.Enqueue((logLevel, formatter(state, exception)));
        }
    }

    // Breaks when a default search_path set for the role in this database, or for the database, lets a tenant connection open:
    // after a rollback or a reset it would fall back to that schema. The open fails with InvalidOperationException; the message
    // names pg_db_role_setting and the kind of statement (ALTER ROLE / ALTER DATABASE ... SET search_path) but NOT the role, the
    // database or the value; those are in Exception.Data and in an error log event. The connection is closed. The database is
    // dropped at the end, and the setting with it.
    [Theory]
    [InlineData("ALTER ROLE")]
    [InlineData("ALTER DATABASE")]
    public async Task ARoleOrDatabaseDefaultSearchPathStopsTheTenantOpen(string form)
    {
        await using var provisioning = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        await using var drop = new DatabaseDrop(postgres, provisioning.Database);
        var (tenant, other) = (Guid.NewGuid(), Guid.NewGuid());
        await provisioning.ProvisionAsync(tenant);
        await provisioning.ProvisionAsync(other);
        var statement = form == "ALTER ROLE"
            ? $"ALTER ROLE {PostgresFixture.Username} IN DATABASE \"{provisioning.Database}\" SET search_path = default_schema_marker"
            : $"ALTER DATABASE \"{provisioning.Database}\" SET search_path = default_schema_marker";
        await provisioning.ExecuteAsync(statement);

        // New sessions only see the default: a second data source, so no pooled session predates the setting.
        var connection = postgres.Connection(provisioning.Database);
        connection.MaxPoolSize = 2;
        var logs = new LogCollector();
        await using var rig = TenantRig.StartOn(
            connection.ConnectionString,
            new Npgsql.NpgsqlDataSourceBuilder(connection.ConnectionString).Build(),
            ownsDataSource: true,
            register: services =>
            {
                TenantRig.StandardModules(services);
                services.AddLogging(builder => builder.AddProvider(logs));
            });
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));

        Assert.Contains("pg_db_role_setting", error.Message, StringComparison.Ordinal);
        Assert.Contains(form, error.Message, StringComparison.Ordinal);
        Assert.Contains("search_path", error.Message, StringComparison.Ordinal);
        foreach (var secret in new[] { PostgresFixture.Username, provisioning.Database, "default_schema_marker", TenantSchemaName.For(other) })
        {
            Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
        }

        var data = string.Join(" ", error.Data.Values.OfType<string>());
        Assert.Contains(provisioning.Database, data, StringComparison.Ordinal);
        Assert.Contains(logs.Events, e => e.Level == LogLevel.Error && e.Text.Contains(provisioning.Database, StringComparison.Ordinal));
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    // Breaks when a database that turns standard_conforming_strings off (which changes how the statement check reads every
    // string) lets a tenant connection open: it must fail closed, naming the setting, with the connection closed.
    [Fact]
    public async Task AStandardConformingStringsDefaultOfOffStopsTheTenantOpen()
    {
        await using var provisioning = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        await using var drop = new DatabaseDrop(postgres, provisioning.Database);
        var tenant = Guid.NewGuid();
        await provisioning.ProvisionAsync(tenant);
        await provisioning.ExecuteAsync($"ALTER DATABASE \"{provisioning.Database}\" SET standard_conforming_strings = off");
        var connection = postgres.Connection(provisioning.Database);
        connection.MaxPoolSize = 2;
        await using var rig = TenantRig.StartOn(
            connection.ConnectionString, new Npgsql.NpgsqlDataSourceBuilder(connection.ConnectionString).Build(), ownsDataSource: true);
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Items.ToListAsync(Token));

        Assert.Contains("standard_conforming_strings", error.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    // Breaks when an Options value that merely contains the words search_path (here as the value of application_name) is
    // refused or ignored, or when the connection stops being isolated.
    [Fact]
    public async Task AnApplicationNameThatContainsSearchPathIsAcceptedAndWorks()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2, tune: c => c.Options = "-c application_name=search_path_app");
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();

        Assert.Equal(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
        Assert.Equal("search_path_app", await context.Database.SqlQueryRaw<string>("SELECT current_setting('application_name') AS \"Value\"").SingleAsync(Token));
    }

    // Breaks when a schema named like the login user is accepted: "$user" in the default path resolves to it, so a connection
    // that lost its binding would read it.
    [Fact]
    public async Task ASchemaNamedLikeTheLoginUserStopsTheTenantOpenAndTheConnectionIsClosed()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        await rig.ExecuteAsync($"CREATE SCHEMA \"{PostgresFixture.Username}\"");
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Items.ToListAsync(Token));

        Assert.Contains("named like the database user", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PostgresFixture.Password, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);

        await rig.ExecuteAsync($"DROP SCHEMA \"{PostgresFixture.Username}\"");
        Assert.Equal(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
    }
}
