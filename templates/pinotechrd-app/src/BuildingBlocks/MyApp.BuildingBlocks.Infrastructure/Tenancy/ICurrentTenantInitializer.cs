using MyApp.BuildingBlocks.Application.Tenancy;

namespace MyApp.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>
/// Sets the tenant of the current scope (see <see cref="ICurrentTenant"/>). It lives in the infrastructure building
/// block, not next to <see cref="ICurrentTenant"/>, so business code can read the tenant but never choose it: only the
/// code that knows the tenant from a trusted source calls it, once per scope (the request pipeline after authentication,
/// a job runner, the migrator).
/// </summary>
public interface ICurrentTenantInitializer
{
    /// <summary>
    /// Sets the tenant of this scope. Setting the same tenant again does nothing; setting another one throws, so a scope
    /// never switches tenants halfway.
    /// </summary>
    /// <param name="tenantId">The opaque identifier of the tenant.</param>
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> is <see cref="Guid.Empty"/>.</exception>
    /// <exception cref="InvalidOperationException">This scope already works for another tenant.</exception>
    void Initialize(Guid tenantId);
}
