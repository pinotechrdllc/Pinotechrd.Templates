using Microsoft.EntityFrameworkCore;
using MyApp.BuildingBlocks.Application.Tenancy;
using MyApp.Modules.Access.Application.Tenants;
using Pinotechrd.Security.Enums;

namespace MyApp.Modules.Access.Infrastructure.Persistence.Catalog;

/// <summary>
/// The <see cref="ITenantCatalog"/> of the catalog: no-tracking reads of <c>catalog.tenants</c>, one query each, never
/// cached, so a suspension applies from the next request. A value that is not a valid slug
/// (<see cref="ProvisionTenantRequest.SlugPattern"/>) is not looked up; the slug reaches SQL only as a parameter.
/// <para>
/// <see cref="CatalogDbContext"/> is a shared context: its connections assume no tenant role (a pooled connection's
/// reset gives back the login's own role), so these reads run as the application's login, which the migrator grants
/// <c>SELECT</c> on <see cref="Columns"/> of <see cref="Table"/> only. A query here touches no other column.
/// </para>
/// </summary>
/// <param name="catalog">The catalog context of the scope.</param>
public sealed class CatalogTenantCatalog(CatalogDbContext catalog) : ITenantCatalog
{
    /// <summary>The catalog table the reads use.</summary>
    public const string Table = "tenants";

    /// <summary>The only columns the reads use, the ones the application's login may read.</summary>
    public static IReadOnlyList<string> Columns { get; } = ["id", "identifier", "status"];

    /// <inheritdoc />
    public async Task<Guid?> FindActiveTenantIdAsync(string slug, CancellationToken cancellationToken = default)
    {
        if (!ProvisionTenantRequest.IsValidSlug(slug))
        {
            return null;
        }

        return await catalog.Tenants.AsNoTracking()
            .Where(t => t.Identifier == slug && t.Status == TenantStatus.Active)
            .Select(t => (Guid?)t.Id)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> IsActiveAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        catalog.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId && t.Status == TenantStatus.Active, cancellationToken);
}
