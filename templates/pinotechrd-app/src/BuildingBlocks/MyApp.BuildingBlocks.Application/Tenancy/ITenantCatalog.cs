namespace MyApp.BuildingBlocks.Application.Tenancy;

/// <summary>
/// Read-only answers about the tenants of the catalog, for the request pipeline: which active tenant a slug names (the
/// tenant chosen before login) and whether a tenant is still active (a token's tenant). Neither answer says why a tenant
/// was not found, so a caller cannot tell an unknown slug from an inactive tenant. The security module implements it.
/// </summary>
public interface ITenantCatalog
{
    /// <summary>
    /// Finds the active tenant whose slug is exactly <paramref name="slug"/>, case included.
    /// </summary>
    /// <param name="slug">The slug as the client sent it; any value is accepted and never quoted.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// The tenant's id, or <see langword="null"/> when the value is not a valid slug, no tenant has it, or its tenant is
    /// not active.
    /// </returns>
    Task<Guid?> FindActiveTenantIdAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>Whether the tenant <paramref name="tenantId"/> exists and is active.</summary>
    /// <param name="tenantId">The tenant's id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> when it is active.</returns>
    Task<bool> IsActiveAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
