namespace MyApp.BuildingBlocks.Infrastructure.Migrations;

/// <summary>
/// The port through which the database migrator learns which tenants exist. The module that owns the tenant list (the
/// Access module, from the catalog) implements it, scoped. Without an implementation the migrator migrates no tenant.
/// </summary>
public interface ITenantDirectory
{
    /// <summary>Lists the id of every tenant, in a stable order.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The tenants' ids.</returns>
    Task<IReadOnlyList<Guid>> ListTenantIdsAsync(CancellationToken cancellationToken = default);
}
