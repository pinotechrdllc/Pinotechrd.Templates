using Microsoft.EntityFrameworkCore;
using MyApp.Modules.Access.Infrastructure.Persistence;
using Pinotechrd.Security.Entities;
using Pinotechrd.Security.Interfaces;
using Pinotechrd.Security.Models;

namespace MyApp.Modules.Access.Infrastructure.Security;

/// <summary>
/// The security library's store with the per-user overrides read under the same rules as the role permissions of
/// <see cref="AccessSecurityDataProvider"/>: none for an inactive user, and only overrides of active permissions of active
/// modules. The library's own query filters by user id alone, so an allow would still grant after the user, the
/// permission or its module was deactivated. A deny dropped here is moot: what it would remove is never granted, because
/// the role path applies the same rules. Every other answer comes from the library's store unchanged.
/// </summary>
/// <param name="inner">The library's store (its cache over the database).</param>
/// <param name="db">The Access context of the scope's tenant.</param>
public sealed class AccessSecurityStore(ISecurityStore inner, AccessDbContext db) : ISecurityStore
{
    /// <inheritdoc />
    public Task<bool> IsSubscriptionActiveAsync(string tenantId, CancellationToken cancellationToken = default) =>
        inner.IsSubscriptionActiveAsync(tenantId, cancellationToken);

    /// <inheritdoc />
    public Task<bool> IsModuleActiveAsync(string tenantId, string moduleCode, CancellationToken cancellationToken = default) =>
        inner.IsModuleActiveAsync(tenantId, moduleCode, cancellationToken);

    /// <inheritdoc />
    public Task<IEnumerable<string>> GetActiveModuleCodesAsync(string tenantId, CancellationToken cancellationToken = default) =>
        inner.GetActiveModuleCodesAsync(tenantId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SecurityModule>> GetActiveCatalogAsync(string tenantId, CancellationToken cancellationToken = default) =>
        inner.GetActiveCatalogAsync(tenantId, cancellationToken);

    /// <summary>
    /// The allow and deny overrides of an active user, on active permissions of active modules, in one query.
    /// </summary>
    /// <param name="userId">The user's identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The overrides; empty when the id is not a GUID or the user is inactive.</returns>
    public async Task<IReadOnlyList<UserPermissionOverride>> GetUserOverridesAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(userId, out var id))
        {
            return [];
        }

        return await (from user in db.Users
                      join row in db.UserModulePermissions on user.Id equals row.UserId
                      where user.Id == id
                            && user.IsActive
                            && row.Permission.IsActive
                            && row.Permission.Module.IsActive
                      select new UserPermissionOverride(row.Permission.Code, row.AccessType))
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
