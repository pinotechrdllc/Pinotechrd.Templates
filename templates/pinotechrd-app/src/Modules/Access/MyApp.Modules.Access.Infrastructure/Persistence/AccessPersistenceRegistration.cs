using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyApp.BuildingBlocks.Application.Security;
using MyApp.BuildingBlocks.Infrastructure.Migrations;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.Modules.Access.Application.Tenants;
using MyApp.Modules.Access.Infrastructure.Persistence.Catalog;
using MyApp.Modules.Access.Infrastructure.Seeding;
using Pinotechrd.Security.Interfaces;

namespace MyApp.Modules.Access.Infrastructure.Persistence;

/// <summary>
/// The persistence of the Access module, shared by the API (through <c>AccessModule</c>) and the migrator, so both build
/// the same tenant context.
/// </summary>
public static class AccessPersistenceRegistration
{
    /// <summary>The module name: it names the history table <c>__ef_migrations_access</c>.</summary>
    public const string ModuleName = "Access";

    /// <summary>
    /// Registers the tenant context <see cref="AccessDbContext"/> (<c>AddTenantDbContext</c>, with the library's model
    /// settings) and the <see cref="ISchemaProvider"/> it requires.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddAccessPersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Added, not tried: it replaces the schema provider the library registers, which could fall back to a shared schema.
        services.AddScoped<ISchemaProvider, AccessSchemaProvider>();
        services.AddTenantDbContext<AccessDbContext>(ModuleName, static (_, options) => AccessDbContext.ConfigureModel(options));
        return services;
    }

    /// <summary>
    /// Registers the shared catalog context <see cref="CatalogDbContext"/> (<c>AddSharedDbContext</c> in
    /// <see cref="CatalogDbContext.Schema"/>), the <see cref="ITenantDirectory"/> that lists the catalog's tenants and the
    /// <see cref="ITenantProvisioner"/>, which needs the tenant migrator (<c>AddDatabaseMigrator</c>). Only the migrator calls
    /// it. The API registers the catalog context alone, for <see cref="CatalogTenantCatalog"/>, and reads only the columns
    /// declared here with <c>AddApplicationRead</c> (<c>catalog.tenants</c>: id, identifier, status), which the migrator
    /// grants the application's login on every run.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddAccessCatalog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSharedDbContext<CatalogDbContext>(CatalogDbContext.Schema);
        // What CatalogTenantCatalog reads in the API, on the application's login: nothing else of the catalog.
        services.AddApplicationRead(CatalogDbContext.Schema, CatalogTenantCatalog.Table, [.. CatalogTenantCatalog.Columns]);
        services.TryAddScoped<ITenantDirectory, CatalogTenantDirectory>();
        services.TryAddScoped<ITenantProvisioner, TenantProvisioner>();
        return services;
    }

    /// <summary>
    /// Registers the seeding of the Access module (ADR 0013), for the migrator only: the <see cref="CatalogPlanSeeder"/>
    /// of the catalog's plans (scoped, on <see cref="CatalogDbContext"/>, so <see cref="AddAccessCatalog"/> comes with it)
    /// and the <see cref="TenantSecuritySeeder"/> the tenant migrator runs for every tenant. Both read the
    /// <see cref="PermissionCatalogue"/>, registered here when no permission is.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddAccessSeeding(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddPermissions();
        services.TryAddScoped<CatalogPlanSeeder>();
        services.AddTenantSeeder<TenantSecuritySeeder>();
        return services;
    }
}
