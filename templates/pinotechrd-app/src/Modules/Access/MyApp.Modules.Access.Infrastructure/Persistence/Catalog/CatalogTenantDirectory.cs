using Microsoft.EntityFrameworkCore;
using MyApp.BuildingBlocks.Infrastructure.Migrations;
using Pinotechrd.Security.Enums;

namespace MyApp.Modules.Access.Infrastructure.Persistence.Catalog;

/// <summary>
/// The <see cref="ITenantDirectory"/> of the catalog: every row of <c>catalog.tenants</c> whatever its status, or only the
/// Active and Suspended ones (in service), ordered by id.
/// </summary>
/// <param name="catalog">The catalog context of the scope.</param>
public sealed class CatalogTenantDirectory(CatalogDbContext catalog) : ITenantDirectory
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> ListTenantIdsAsync(CancellationToken cancellationToken = default) =>
        await catalog.Tenants.AsNoTracking().OrderBy(t => t.Id).Select(t => t.Id).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>The Active and Suspended tenants: Provisioning ones have no schema yet, Cancelled ones are left alone.</remarks>
    public async Task<IReadOnlyList<Guid>> ListInServiceTenantIdsAsync(CancellationToken cancellationToken = default) =>
        await catalog.Tenants.AsNoTracking()
            .Where(t => t.Id != Guid.Empty && (t.Status == TenantStatus.Active || t.Status == TenantStatus.Suspended))
            .OrderBy(t => t.Id)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
}
