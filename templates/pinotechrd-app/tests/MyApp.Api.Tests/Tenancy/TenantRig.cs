using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Npgsql;

namespace MyApp.Api.Tests.Tenancy;

/// <summary>
/// A plain service provider (no web services) on a database of its own in the shared container, with the fixture
/// modules registered through the production API, a data source whose pool is small, and a count of the commands that
/// reach the server. Administrative reads use connections outside the pool under test.
/// </summary>
internal sealed class TenantRig : IAsyncDisposable
{
    private readonly string _adminConnectionString;
    private readonly CommandCounter _commands;

    private readonly bool _ownsDataSource;

    private TenantRig(
        NpgsqlDataSource dataSource, ServiceProvider services, string database, string adminConnectionString, CommandCounter commands, bool ownsDataSource)
    {
        _ownsDataSource = ownsDataSource;
        DataSource = dataSource;
        Services = services;
        Database = database;
        _adminConnectionString = adminConnectionString;
        _commands = commands;
    }

    public NpgsqlDataSource DataSource { get; }

    public ServiceProvider Services { get; }

    public string Database { get; }

    public string AdminConnectionString => _adminConnectionString;

    /// <summary>The connection string of the rig's database as pooled connections use it.</summary>
    public string ConnectionString => DataSource.ConnectionString;

    /// <summary>How many commands the rig's database has received from Npgsql so far (as its activity source reports them).</summary>
    public int CommandsSent => _commands.Count;

    /// <summary>Registers Access, Billing and Sales, deliberately not in their migration order, and the migrator.</summary>
    public static void StandardModules(IServiceCollection services)
    {
        services.AddTenantDbContext<SalesContext>("Sales");
        services.AddTenantDbContext<BillingContext>("Billing");
        services.AddTenantDbContext<AccessContext>("Access");
        services.AddTenantMigrator();
    }

    public static async Task<TenantRig> StartAsync(
        PostgresFixture postgres,
        int maxPoolSize = 8,
        Action<IServiceCollection>? register = null,
        bool validateScopes = true,
        Action<NpgsqlConnectionStringBuilder>? tune = null)
    {
        var connection = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync()) { MaxPoolSize = maxPoolSize };
        tune?.Invoke(connection);
        return StartOn(connection.ConnectionString, new NpgsqlDataSourceBuilder(connection.ConnectionString).Build(), ownsDataSource: true, register, validateScopes);
    }

    /// <summary>A rig on a database and a data source that already exist (a second migrator on one database, or the host's own data source).</summary>
    public static TenantRig StartOn(
        string connectionString, NpgsqlDataSource dataSource, bool ownsDataSource, Action<IServiceCollection>? register = null, bool validateScopes = true)
    {
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        var admin = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        var commands = new CommandCounter(connection.Database!);
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        (register ?? StandardModules)(services);
        return new TenantRig(
            dataSource,
            services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = validateScopes }),
            connection.Database!,
            admin.ConnectionString,
            commands,
            ownsDataSource);
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        if (_ownsDataSource)
        {
            await DataSource.DisposeAsync();
        }

        _commands.Dispose();
    }

    /// <summary>A scope whose tenant is set, as the code that knows the tenant would do it.</summary>
    public AsyncServiceScope ScopeFor(Guid tenantId)
    {
        var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICurrentTenantInitializer>().Initialize(tenantId);
        return scope;
    }

    /// <summary>
    /// Creates the tenant's schema and applies every module's migrations without the migrator (plain SQL and each
    /// context's own migrate), so a test of the contexts does not depend on the migrator.
    /// </summary>
    public async Task ProvisionAsync(Guid tenantId)
    {
        await ExecuteAsync($"CREATE SCHEMA \"{TenantSchemaName.For(tenantId)}\"");
        await using var scope = ScopeFor(tenantId);
        foreach (var module in scope.ServiceProvider.GetServices<IModuleMigrations>())
        {
            await module.GetContext(scope.ServiceProvider).Database.MigrateAsync(TestContext.Current.CancellationToken);
        }
    }

    public async Task<NpgsqlConnection> OpenAdminAsync()
    {
        var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

#pragma warning disable CA2100 // The SQL of these helpers is written by the tests, with schema names made from GUIDs.
    public async Task ExecuteAsync(string sql)
    {
        await using var connection = await OpenAdminAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = await OpenAdminAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>The first column of every row, as text, in the order the query gives.</summary>
    public async Task<List<string>> ColumnAsync(string sql)
    {
        await using var connection = await OpenAdminAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);
        }

        return rows;
    }
#pragma warning restore CA2100

    public Task<bool> SchemaExistsAsync(string schema) =>
        ScalarAsync<bool>($"SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = '{schema}')");

    /// <summary>The tables of every schema that is not a system schema, as <c>schema.table</c>.</summary>
    public Task<List<string>> UserTablesAsync() =>
        ColumnAsync(
            "SELECT table_schema || '.' || table_name FROM information_schema.tables "
            + "WHERE table_schema NOT IN ('pg_catalog', 'information_schema') ORDER BY 1");

    /// <summary>The migration ids in a module's history table in a tenant's schema, ordered.</summary>
    public Task<List<string>> HistoryAsync(Guid tenantId, string module) =>
        ColumnAsync(
            $"SELECT migration_id FROM \"{TenantSchemaName.For(tenantId)}\".\"{TenantPersistenceRegistration.HistoryTableOf(module)}\" ORDER BY migration_id");

    /// <summary>Counts the commands of one database that Npgsql reports through its activity source.</summary>
    private sealed class CommandCounter : IDisposable
    {
        private readonly ConcurrentQueue<string> _seen = new();
        private readonly ActivityListener _listener;

        public CommandCounter(string database)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Npgsql",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    var text = activity.GetTagItem("db.query.text") as string;
                    var named = activity.TagObjects.Any(t => string.Equals(t.Value as string, database, StringComparison.Ordinal));
                    if (text is not null && named)
                    {
                        _seen.Enqueue(text);
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public int Count => _seen.Count;

        public void Dispose() => _listener.Dispose();
    }
}
