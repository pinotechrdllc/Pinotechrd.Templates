using Microsoft.Extensions.Hosting;
using MyApp.Api.Tests.Database;
using MyApp.Migrator.Tests.Support;
using Npgsql;
using Pinotechrd.Security.Entities;

namespace MyApp.Migrator.Tests.Provisioning;

/// <summary>A row of <c>catalog.tenants</c> as the tests read it with plain SQL.</summary>
internal sealed record TenantRow(
    Guid Id, string Name, Guid SubscriptionId, int Status, int Attempts, DateTime? CompletedAt, string? Error, string? DbSchema);

/// <summary>Helpers shared by the provisioning tests: the command line, the catalog rows and the status values.</summary>
internal static class ProvisionSupport
{
    public const string Verb = "provision";

    // The integer of a status of the library's tenant status enum, read from the type of Tenant.Status by name.
    private static int StatusValue(string name) =>
        Convert.ToInt32(Enum.Parse(typeof(Tenant).GetProperty(nameof(Tenant.Status))!.PropertyType, name), System.Globalization.CultureInfo.InvariantCulture);

    public static int InProvisioning => StatusValue("Provisioning");

    public static int Active => StatusValue("Active");

    public static int Suspended => StatusValue("Suspended");

    /// <summary>Runs <c>provision</c> with these settings; a null one is left off the command line.</summary>
    public static Task<MigratorRun> RunAsync(string database, string? slug, string? name, string? subscription)
    {
        var arguments = new List<string> { Verb, $"--{MigratorRun.ConnectionKey}={database}" };
        if (slug is not null)
        {
            arguments.Add($"--Provision:Slug={slug}");
        }

        if (name is not null)
        {
            arguments.Add($"--Provision:Name={name}");
        }

        if (subscription is not null)
        {
            arguments.Add($"--Provision:SubscriptionId={subscription}");
        }

        return MigratorRun.ExecuteRawAsync([.. arguments]);
    }

    public static Task<MigratorRun> RunAsync(string database, string slug, string name, Guid subscription) =>
        RunAsync(database, slug, name, subscription.ToString("D"));

    /// <summary>A new database with the catalog migrated and one subscription.</summary>
    public static async Task<(string Database, Guid Subscription)> ArrangeAsync(PostgresFixture postgres)
    {
        var database = await postgres.CreateDatabaseAsync();
        var migrated = await MigratorRun.ExecuteAsync(database);
        Assert.True(migrated.ExitCode == 0, migrated.Report);
        return (database, await CatalogSeed.AddSubscriptionAsync(database));
    }

    public static async Task<List<TenantRow>> RowsAsync(string database, string? identifier = null)
    {
        await using var connection = await Db.OpenAsync(database);
        await using var command = new NpgsqlCommand(
            "SELECT id, name, subscription_id, status, provisioning_attempts, provisioning_completed_at, provisioning_error, db_schema "
            + "FROM catalog.tenants WHERE $1::text IS NULL OR identifier = $1 ORDER BY identifier",
            connection);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)identifier ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<TenantRow>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new TenantRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetGuid(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return rows;
    }

    public static async Task<List<string>> HistoryAsync(string database, Guid tenant) =>
        await Db.ColumnAsync(
            database,
            $"SELECT migration_id FROM \"{MyApp.BuildingBlocks.Infrastructure.Tenancy.TenantSchemaName.For(tenant)}\".__ef_migrations_access ORDER BY migration_id");

    public static async Task<long> TenantSchemaCountAsync(string database) =>
        await Db.CountAsync(database, "SELECT count(*) FROM pg_namespace WHERE left(nspname, 7) = 'tenant_'");

    /// <summary>
    /// The tables a migrated Access tenant schema has, taken from a tenant row the plain <c>migrate</c> command migrates,
    /// so the expectation is the migrator's own and not a list typed here.
    /// </summary>
    public static async Task<List<string>> ReferenceTablesAsync(string database)
    {
        var reference = Guid.NewGuid();
        await CatalogSeed.AddTenantAsync(database, reference);
        var run = await MigratorRun.ExecuteAsync(database);
        Assert.True(run.ExitCode == 0, run.Report);
        return await Db.TablesOfAsync(database, MyApp.BuildingBlocks.Infrastructure.Tenancy.TenantSchemaName.For(reference));
    }
}

/// <summary>The migrator's own composition, started in process, to resolve the provisioner the way a caller would.</summary>
internal sealed class ProvisioningHost : IAsyncDisposable
{
    private readonly IHost _host;

    private ProvisioningHost(IHost host) => _host = host;

    public IServiceProvider Services => _host.Services;

    public static async Task<ProvisioningHost> StartAsync(string database)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [$"--{MigratorRun.ConnectionKey}={database}"],
            ApplicationName = typeof(MigratorHost).Assembly.GetName().Name,
        });
        builder.AddMigrations();
        var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return new ProvisioningHost(host);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(CancellationToken.None);
        _host.Dispose();
    }
}
