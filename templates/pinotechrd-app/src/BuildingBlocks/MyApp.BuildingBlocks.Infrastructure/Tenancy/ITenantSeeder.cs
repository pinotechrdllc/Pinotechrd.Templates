namespace MyApp.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>
/// Data a tenant's schema must hold, kept in step with the code by the tenant migrator: after every module of the tenant
/// is migrated, in the same scope (the tenant set in migration mode, so its connections keep the migrator's login) and
/// under the same tenant lock, the migrator runs each registered seeder in registration order. A seeder that throws fails
/// that tenant's run, like a failed migration; the other tenants go on. Seeders run on every migration of every tenant,
/// so they only add what is missing (idempotent, safe to run again after a failure). Register one with
/// <see cref="TenantPersistenceRegistration.AddTenantSeeder{TSeeder}"/>.
/// </summary>
public interface ITenantSeeder
{
    /// <summary>Adds what the tenant's schema lacks.</summary>
    /// <param name="scopedServices">
    /// The services of the migration scope, whose tenant is set: resolve the module's context here. The scope owns it.
    /// </param>
    /// <param name="tenantId">The tenant's id.</param>
    /// <param name="cancellationToken">Stops the seeding.</param>
    /// <returns>A task that completes when the seeding is done.</returns>
    Task SeedAsync(IServiceProvider scopedServices, Guid tenantId, CancellationToken cancellationToken);
}
