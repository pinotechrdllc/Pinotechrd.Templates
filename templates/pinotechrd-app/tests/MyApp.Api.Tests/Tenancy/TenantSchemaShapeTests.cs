using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;

namespace MyApp.Api.Tests.Tenancy;

// What a migrated tenant schema looks like: the same tables and columns for every tenant, snake_case names, one history
// table per module inside the tenant's schema, and migrations that name no schema. Needs Docker.
[Collection(PostgresContainer.Name)]
public class TenantSchemaShapeTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly Regex SnakeCase = new("^[a-z_][a-z0-9_]*$", RegexOptions.CultureInvariant);

    private static Task<List<string>> ColumnsOfAsync(TenantRig rig, Guid tenant) =>
        rig.ColumnAsync(
            "SELECT table_name || '|' || column_name || '|' || data_type || '|' || is_nullable || '|' || ordinal_position "
            + $"FROM information_schema.columns WHERE table_schema = '{TenantSchemaName.For(tenant)}' ORDER BY table_name, ordinal_position");

    // Breaks when two tenants end up with different tables or columns (a model that depends on the tenant), when a name is
    // not snake_case, when a history table is missing from the tenant's schema (or sits in public), or when any table
    // lands in public.
    [Fact]
    public async Task TwoTenantsGetIdenticalSnakeCaseTablesAndEachModulesHistoryTableInItsOwnSchema()
    {
        await using var rig = await TenantRig.StartAsync(postgres);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var migrator = rig.Services.GetRequiredService<ITenantMigrator>();
        Assert.True((await migrator.MigrateAsync(a, Token)).Succeeded);
        Assert.True((await migrator.MigrateAsync(b, Token)).Succeeded);

        var columnsA = await ColumnsOfAsync(rig, a);
        var columnsB = await ColumnsOfAsync(rig, b);

        Assert.NotEmpty(columnsA);
        Assert.Equal(columnsA, columnsB);
        var tables = columnsA.Select(c => c.Split('|')[0]).Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            ["__ef_migrations_access", "__ef_migrations_billing", "__ef_migrations_sales", "access_items", "billing_items", "sales_items"],
            tables);
        Assert.All(columnsA, c => Assert.All(c.Split('|')[..2], name => Assert.Matches(SnakeCase, name)));
        Assert.Contains("access_items|created_by|text|YES|3", columnsA);
        Assert.Contains("__ef_migrations_access|migration_id|character varying|NO|1", columnsA);
        Assert.Contains("__ef_migrations_access|product_version|character varying|NO|2", columnsA);
        Assert.DoesNotContain(await rig.UserTablesAsync(), t => t.StartsWith("public.", StringComparison.Ordinal));
        Assert.Equal(12, (await rig.UserTablesAsync()).Count);
    }

    // Breaks when the generated script of a module qualifies a name with a schema or carries the tenant's schema (a
    // migration with a schema in it would serve one tenant only). The script comes from the design-time options, the
    // same configuration `dotnet ef` uses, and from a context of a tenant's scope at run time.
    [Theory]
    [InlineData("Access")]
    [InlineData("Billing")]
    [InlineData("Sales")]
    public async Task TheGeneratedMigrationScriptNamesNoSchema(string module)
    {
        DbContext designTime = module switch
        {
            "Access" => new AccessContext(TenantPersistenceRegistration.DesignTimeOptions<AccessContext>(module)),
            "Billing" => new BillingContext(TenantPersistenceRegistration.DesignTimeOptions<BillingContext>(module)),
            _ => new SalesContext(TenantPersistenceRegistration.DesignTimeOptions<SalesContext>(module)),
        };
        string designScript;
        await using (designTime)
        {
            designScript = designTime.GetService<IMigrator>().GenerateScript();
        }

        await using var rig = await TenantRig.StartAsync(postgres);
        var tenant = Guid.NewGuid();
        string runtimeScript;
        await using (var scope = rig.ScopeFor(tenant))
        {
            var context = scope.ServiceProvider.GetServices<IModuleMigrations>().Single(m => m.ModuleName == module).GetContext(scope.ServiceProvider);
            runtimeScript = context.GetService<IMigrator>().GenerateScript();
        }

        foreach (var script in new[] { designScript, runtimeScript })
        {
            var history = TenantPersistenceRegistration.HistoryTableOf(module);
            Assert.Contains($"CREATE TABLE IF NOT EXISTS {history}", script, StringComparison.Ordinal);
            Assert.Contains($"CREATE TABLE {module.ToLowerInvariant()}_items", script, StringComparison.Ordinal);
            Assert.DoesNotContain("tenant_", script, StringComparison.Ordinal);
            Assert.DoesNotContain("public", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CREATE SCHEMA", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(new Regex("(?:\"[^\"]+\"|\\b[a-z_][a-z0-9_]*)\\.(?:\"[^\"]+\"|[a-z_])", RegexOptions.CultureInvariant), script);
            Assert.DoesNotContain(await rig.UserTablesAsync(), t => t.Contains(history, StringComparison.Ordinal));
        }
    }

    // Breaks when the design-time options carry a connection (they must never reach a database), or lose the history table.
    [Fact]
    public void DesignTimeOptionsHaveNoConnectionAndTheModulesHistoryTable()
    {
        using var context = new AccessContext(TenantPersistenceRegistration.DesignTimeOptions<AccessContext>("Access"));

        Assert.Null(context.Database.GetConnectionString());
        Assert.Contains("__ef_migrations_access", context.GetService<IHistoryRepository>().GetCreateIfNotExistsScript(), StringComparison.Ordinal);
    }
}
