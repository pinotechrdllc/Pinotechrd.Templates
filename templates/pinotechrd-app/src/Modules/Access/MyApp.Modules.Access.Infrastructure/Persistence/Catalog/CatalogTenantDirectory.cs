using Microsoft.EntityFrameworkCore;
using MyApp.BuildingBlocks.Infrastructure.Migrations;

namespace MyApp.Modules.Access.Infrastructure.Persistence.Catalog;

/// <summary>The <see cref="ITenantDirectory"/> of the catalog: every row of <c>catalog.tenants</c>, whatever its status, ordered by id.</summary>
/// <param name="catalog">The catalog context of the scope.</param>
public sealed class CatalogTenantDirectory(CatalogDbContext catalog) : ITenantDirectory
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> ListTenantIdsAsync(CancellationToken cancellationToken = default) =>
        await catalog.Tenants.AsNoTracking().OrderBy(t => t.Id).Select(t => t.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
}
