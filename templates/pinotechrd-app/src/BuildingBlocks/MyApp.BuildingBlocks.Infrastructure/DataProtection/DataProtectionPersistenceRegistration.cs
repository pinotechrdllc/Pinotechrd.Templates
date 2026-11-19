using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyApp.BuildingBlocks.Infrastructure.Migrations;

namespace MyApp.BuildingBlocks.Infrastructure.DataProtection;

/// <summary>
/// The storage of the Data Protection key ring in the shared database. It knows nothing of ASP.NET Core: the API adapts
/// <see cref="DataProtectionKeyStore"/> to the key manager, and the migrator migrates the context and grants the
/// application's login what the store uses.
/// </summary>
public static class DataProtectionPersistenceRegistration
{
    /// <summary>
    /// Registers <see cref="DataProtectionKeysDbContext"/> as a shared context (schema
    /// <see cref="DataProtectionKeysDbContext.Schema"/>), the singleton <see cref="DataProtectionKeyStore"/>, and what the
    /// application's login may do on the table: read <see cref="DataProtectionKeyStore.ReadColumns"/> and insert
    /// <see cref="DataProtectionKeyStore.InsertColumns"/>, never update or delete. Both the API and the migrator call it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddDataProtectionKeyStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSharedDbContext<DataProtectionKeysDbContext>(DataProtectionKeysDbContext.Schema);
        if (services.Any(d => d.ServiceType == typeof(DataProtectionKeyStore)))
        {
            return services;
        }

        services.AddApplicationRead(DataProtectionKeysDbContext.Schema, DataProtectionKeysDbContext.Table, [.. DataProtectionKeyStore.ReadColumns]);
        services.AddApplicationAppend(DataProtectionKeysDbContext.Schema, DataProtectionKeysDbContext.Table, [.. DataProtectionKeyStore.InsertColumns]);
        services.TryAddSingleton<DataProtectionKeyStore>();
        return services;
    }
}
