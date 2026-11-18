using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Pinotechrd.Security.Data;
using Pinotechrd.Security.Entities;
using Pinotechrd.Security.Tenancy;

namespace Pinotechrd.Spikes.SecuritySchemaPerTenant;

/// <summary>Q1: the model. Also provisions the catalog and the two tenant schemas from the generated scripts.</summary>
public static partial class Q1Model
{
    [GeneratedRegex("""CREATE TABLE (\S+) \(""")]
    private static partial Regex CreateTable();

    public static async Task<Outcome> RunAsync(IServiceProvider services, NpgsqlDataSource shared)
    {
        var q = new Outcome("Q1", required: true);

        // What the library does on its own, as documented (no override).
        var plain = new DbContextOptionsBuilder<PinotechrdUnmodifiedDbContext>().UseNpgsql(shared).UseSnakeCaseNamingConvention().Options;
        await using (var ua = new PinotechrdUnmodifiedDbContext(plain, new FixedSchemaProvider("tenant_a")))
        await using (var ub = new PinotechrdUnmodifiedDbContext(plain, new FixedSchemaProvider("tenant_b")))
        {
            q.Fact($"library as is, default EF cache key: same model for tenant_a and tenant_b = {ReferenceEquals(ua.Model, ub.Model)}; " +
                $"users schema seen by tenant_b = '{ModelFacts.SchemaOf<AppUser>(ub.Model)}'; tenants schema = '{ModelFacts.SchemaOf<Tenant>(ub.Model)}'");
        }

        var keyed = new DbContextOptionsBuilder<PinotechrdUnmodifiedDbContext>().UseNpgsql(shared).UseSnakeCaseNamingConvention()
            .ReplaceService<IModelCacheKeyFactory, ModelSchemaCacheKeyFactory>().Options;
        await using (var ka = new PinotechrdUnmodifiedDbContext(keyed, new FixedSchemaProvider("tenant_a")))
        await using (var kb = new PinotechrdUnmodifiedDbContext(keyed, new FixedSchemaProvider("tenant_b")))
        {
            q.Fact($"library with its ModelSchemaCacheKeyFactory (not registered by AddPinotechrdSecurity): same model = {ReferenceEquals(ka.Model, kb.Model)}; " +
                $"users schema seen by tenant_b = '{ModelFacts.SchemaOf<AppUser>(kb.Model)}' (one model per tenant)");
        }

        // The remapped context, resolved the way a request resolves it.
        await using var scopeA = services.CreateAsyncScope();
        await using var scopeB = services.CreateAsyncScope();
        scopeA.ServiceProvider.GetRequiredService<TenantContext>().SetTenant("a");
        scopeB.ServiceProvider.GetRequiredService<TenantContext>().SetTenant("b");
        var a = scopeA.ServiceProvider.GetRequiredService<PinotechrdTenantSecurityDbContext>();
        var b = scopeB.ServiceProvider.GetRequiredService<PinotechrdTenantSecurityDbContext>();
        var catalog = scopeA.ServiceProvider.GetRequiredService<PinotechrdCatalogDbContext>();

        // Migration settings live only in the design-time model; the runtime model is checked for identity below.
        var model = a.GetService<IDesignTimeModel>().Model;
        var tenantTables = model.GetEntityTypes().Where(e => !PinotechrdSchemaMapping.CatalogTypes.Contains(e.ClrType)).ToList();
        var catalogTables = model.GetEntityTypes().Where(e => PinotechrdSchemaMapping.CatalogTypes.Contains(e.ClrType)).ToList();

        q.Check(tenantTables.All(e => e.GetSchema() is null) && model.GetDefaultSchema() is null,
            $"(a) {tenantTables.Count} tenant entities and the default schema have no schema");
        q.Check(catalogTables.All(e => e.GetSchema() == PinotechrdSchemaMapping.CatalogSchema && e.IsTableExcludedFromMigrations()),
            $"(b) {catalogTables.Count} catalog entities in 'catalog' and excluded from tenant migrations: " +
            string.Join(", ", catalogTables.Select(e => e.GetTableName())));
        var crossing = model.GetEntityTypes().SelectMany(e => e.GetForeignKeys())
            .Where(fk => PinotechrdSchemaMapping.CatalogTypes.Contains(fk.DeclaringEntityType.ClrType)
                != PinotechrdSchemaMapping.CatalogTypes.Contains(fk.PrincipalEntityType.ClrType)).ToList();
        q.Check(crossing.Count == 0,
            "(c) no foreign key crosses catalog and tenant: ignoring SubscriptionPlanModule.Module and SecurityModule.PlanModules dropped " +
            "the library's plan module -> security module key; further keys the override had to remove: " +
            (PinotechrdTenantSecurityDbContext.RemovedForeignKeys.Count == 0 ? "none" : string.Join(", ", PinotechrdTenantSecurityDbContext.RemovedForeignKeys)));
        q.Check(ReferenceEquals(a.Model, b.Model), "(d) tenants a and b get the same model instance (default EF cache key, no factory replaced)");

        var tenantScript = Script(a);
        var catalogScript = Script(catalog);
        var tenantCreates = CreateTable().Matches(tenantScript).Select(m => m.Groups[1].Value).ToList();
        var catalogCreates = CreateTable().Matches(catalogScript).Select(m => m.Groups[1].Value).ToList();
        q.Fact($"tenant script: {tenantCreates.Count} tables: {string.Join(", ", tenantCreates)}");
        q.Fact($"catalog script: {catalogCreates.Count} tables: {string.Join(", ", catalogCreates)}");
        // "IDENTITY" alone is legal (identity columns of the int keys); a schema shows as "x". or x.
        string[] forbidden = ["tenant_a", "tenant_b", "public.", "\"public\"", "identity.", "\"identity\"",
            ModelBuildingSchemaProvider.Placeholder, "catalog", "CREATE SCHEMA"];
        var leaked = forbidden.Where(f => tenantScript.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        q.Check(leaked.Count == 0 && tenantCreates.All(t => !t.Contains('.')),
            "(d) tenant script names no schema at all (no tenant_a/b, public, identity, catalog, placeholder, CREATE SCHEMA)" +
            (leaked.Count == 0 ? string.Empty : $"; found: {string.Join(", ", leaked)}"));
        q.Check(catalogCreates.All(t => t.StartsWith("catalog.", StringComparison.Ordinal)) && catalogCreates.Count == catalogTables.Count
            && !catalogScript.Contains("security_modules", StringComparison.Ordinal),
            "catalog script: only catalog.* tables, no reference to a tenant table");

        // Provision: catalog once; each tenant through the interceptor, i.e. the unqualified script lands in search_path.
        await Exec(shared, $"CREATE SCHEMA {PinotechrdSchemaMapping.CatalogSchema}");
        await Exec(shared, catalogScript);
        await Exec(shared, DbRefreshTokenStore.CreateTableSql);
        foreach (var (tenant, ctx) in new[] { ("tenant_a", a), ("tenant_b", b) })
        {
            await Exec(shared, $"CREATE SCHEMA {TenantSchemas.Validate(tenant)}");
            await ctx.Database.ExecuteSqlRawAsync(tenantScript);
        }

        var tablesA = await Tables(shared, "tenant_a");
        var tablesB = await Tables(shared, "tenant_b");
        var tablesCatalog = await Tables(shared, "catalog");
        var tablesPublic = await Tables(shared, "public");
        q.Check(tablesA.SequenceEqual(tablesB) && tablesA.Count == tenantCreates.Count,
            $"one script applied through the interceptor: tenant_a and tenant_b each have the same {tablesA.Count} tables");
        q.Check(tablesPublic.Count == 0, $"public has {tablesPublic.Count} tables; catalog has {tablesCatalog.Count} (incl. refresh_tokens of Q3)");

        q.Summary = q.Passed
            ? "override of OnModelCreating on the library context works (schema removed, catalog in 'catalog' and excluded, " +
              "plan-module -> module navigations ignored, one cached model, script with no schema). Library hard-codes 'public' and a per-tenant schema in the model."
            : "the override did not meet (a)-(d)";
        return q;
    }

    /// <summary>The SQL of an initial migration: the same differ and generator 'migrations add' and 'migrations script' use.</summary>
    public static string Script(DbContext context)
    {
        var designModel = context.GetService<IDesignTimeModel>().Model;
        var operations = context.GetService<IMigrationsModelDiffer>().GetDifferences(null, designModel.GetRelationalModel());
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(operations, designModel);
        return string.Join("\n", commands.Select(c => c.CommandText));
    }

    public static async Task Exec(NpgsqlDataSource ds, string sql)
    {
        await using var cmd = ds.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<List<string>> Tables(NpgsqlDataSource ds, string schema)
    {
        await using var cmd = ds.CreateCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = $1 ORDER BY table_name");
        cmd.Parameters.AddWithValue(schema);
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<string>();
        while (await reader.ReadAsync())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }
}
