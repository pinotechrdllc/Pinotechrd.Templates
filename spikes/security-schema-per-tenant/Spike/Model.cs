using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Pinotechrd.Security.Data;
using Pinotechrd.Security.Entities;
using Pinotechrd.Security.Interfaces;
using Pinotechrd.Security.Tenancy;

namespace Pinotechrd.Spikes.SecuritySchemaPerTenant;

public sealed class AppUser : BaseSecurityUser;

public sealed class AppRole : BaseSecurityRole;

/// <summary>
/// Answers the library's <see cref="ISchemaProvider"/> with one constant value. The library's model reads it once
/// while building; the override below removes every use of it, so the value never reaches SQL.
/// </summary>
public sealed class ModelBuildingSchemaProvider : ISchemaProvider
{
    public const string Placeholder = "pinotechrd_model_placeholder";
    public static readonly ModelBuildingSchemaProvider Instance = new();

    public string GetSchemaName() => Placeholder;

    public string GetDefaultSchema() => Placeholder;
}

/// <summary>The remapping both contexts apply on top of the library's <c>OnModelCreating</c>.</summary>
public static class PinotechrdSchemaMapping
{
    public const string CatalogSchema = "catalog";

    /// <summary>The entities the library maps to its hard-coded "public" catalog schema.</summary>
    public static readonly IReadOnlySet<Type> CatalogTypes = new HashSet<Type>
    {
        typeof(Tenant), typeof(SubscriptionPlan), typeof(SystemSubscription),
        typeof(SubscriptionPlanModule), typeof(SubscriptionPlanFeature),
    };

    /// <summary>
    /// Tenant tables lose every schema (resolved by search_path); catalog tables go to the constant "catalog" schema;
    /// the one relationship that crosses the two (plan module -> tenant security module) is removed.
    /// </summary>
    /// <param name="ownsCatalog">True for the catalog context (migrates catalog, not tenant tables); false for the tenant context.</param>
    /// <returns>The foreign keys removed because they crossed catalog and tenant tables.</returns>
    public static List<string> Apply(ModelBuilder builder, bool ownsCatalog)
    {
        builder.HasDefaultSchema(null);

        // The library joins plan modules to modules by ModuleId only (DbSecurityStore), never through these navigations.
        builder.Entity<SubscriptionPlanModule>().Ignore(e => e.Module);
        builder.Entity<SecurityModule>().Ignore(e => e.PlanModules);

        var removed = new List<string>();
        foreach (var entity in builder.Model.GetEntityTypes().ToList())
        {
            var isCatalog = CatalogTypes.Contains(entity.ClrType);
            foreach (var fk in entity.GetForeignKeys().ToList())
            {
                if (CatalogTypes.Contains(fk.PrincipalEntityType.ClrType) != isCatalog)
                {
                    removed.Add($"{entity.ClrType.Name}->{fk.PrincipalEntityType.ClrType.Name}");
                    entity.RemoveForeignKey(fk);
                }
            }

            entity.SetSchema(isCatalog ? CatalogSchema : null);
            entity.SetIsTableExcludedFromMigrations(isCatalog != ownsCatalog);
        }

        return removed;
    }
}

/// <summary>Contexts whose connections must be bound to the current tenant's schema.</summary>
public interface ITenantSchemaContext
{
    /// <summary>The schema of the current tenant, or null when no tenant is resolved.</summary>
    string? TenantSchema { get; }
}

/// <summary>
/// The path Q1 measures: the library's context, remapped. No schema in tenant tables, catalog in "catalog" and
/// excluded from the tenant migrations, no cross-schema foreign key, one cached model for every tenant.
/// </summary>
public sealed class PinotechrdTenantSecurityDbContext(
    DbContextOptions<PinotechrdTenantSecurityDbContext> options, TenantContext tenant)
    : PinotechrdSecurityDbContext<AppUser, AppRole>(options, ModelBuildingSchemaProvider.Instance), ITenantSchemaContext
{
    public static List<string> RemovedForeignKeys { get; private set; } = [];

    public string? TenantSchema => tenant.SchemaName;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        RemovedForeignKeys = PinotechrdSchemaMapping.Apply(modelBuilder, ownsCatalog: false);
    }
}

/// <summary>Same mapping, but it migrates the catalog tables and excludes the tenant tables. Never bound to a tenant.</summary>
public sealed class PinotechrdCatalogDbContext(DbContextOptions<PinotechrdCatalogDbContext> options)
    : PinotechrdSecurityDbContext<AppUser, AppRole>(options, ModelBuildingSchemaProvider.Instance)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        PinotechrdSchemaMapping.Apply(modelBuilder, ownsCatalog: true);
    }
}

/// <summary>The library's context used as documented, with no override: for contrast only.</summary>
public sealed class PinotechrdUnmodifiedDbContext(DbContextOptions<PinotechrdUnmodifiedDbContext> options, ISchemaProvider schema)
    : PinotechrdSecurityDbContext<AppUser, AppRole>(options, schema);

public sealed class FixedSchemaProvider(string schema) : ISchemaProvider
{
    public string GetSchemaName() => schema;

    public string GetDefaultSchema() => schema;
}

public static class ModelFacts
{
    public static string? SchemaOf<T>(IModel model) => model.FindEntityType(typeof(T))?.GetSchema();
}
