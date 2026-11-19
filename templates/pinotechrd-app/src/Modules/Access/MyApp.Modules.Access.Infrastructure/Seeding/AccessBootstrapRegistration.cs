using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.Modules.Access.Infrastructure.Persistence;
using MyApp.Modules.Access.Infrastructure.Persistence.Catalog;
using MyApp.Modules.Access.Infrastructure.Security;

namespace MyApp.Modules.Access.Infrastructure.Seeding;

/// <summary>The registrations of the migrator's <c>bootstrap</c> command in the Access module (ADR 0013).</summary>
public static class AccessBootstrapRegistration
{
    /// <summary>
    /// Registers what the bootstrap command needs on top of the seeding (<c>AddAccessSeeding</c>, called first): the
    /// <see cref="BootstrapTarget"/>, Identity's user manager on <see cref="AccessDbContext"/> with the Access password
    /// policy, and the <see cref="OperatorAdminSeeder"/>, which runs after the tenant's security seeding.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddAccessBootstrap(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<BootstrapTarget>();
        // The provisioner by its type, for TenantProvisioner.ProvisionOperatorAsync: the only way onto the operator subscription.
        services.TryAddScoped<TenantProvisioner>();
        services.AddIdentityCore<AccessUser>()
            .AddRoles<AccessRole>()
            .AddEntityFrameworkStores<AccessDbContext>();
        services.AddAccessPasswordPolicy();
        services.AddTenantSeeder<OperatorAdminSeeder>();
        return services;
    }
}
