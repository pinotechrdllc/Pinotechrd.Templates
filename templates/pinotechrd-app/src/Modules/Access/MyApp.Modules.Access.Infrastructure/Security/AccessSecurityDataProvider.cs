using Microsoft.EntityFrameworkCore;
using MyApp.BuildingBlocks.Application.Identity;
using MyApp.BuildingBlocks.Application.Tenancy;
using MyApp.Modules.Access.Infrastructure.Persistence;
using Pinotechrd.Security.Interfaces;

namespace MyApp.Modules.Access.Infrastructure.Security;

/// <summary>
/// What the security library's engine asks the application: the current user and tenant, from <see cref="ICurrentUser"/>
/// and <see cref="ICurrentTenant"/> (GUIDs in their lower-case <c>D</c> form, as the library compares them), and the
/// roles and role permissions of a user, read from the tenant's own tables. The <c>permission</c> claims of a token are
/// never read: a permission granted or taken away counts from the next request (see the README for the library's cache).
/// </summary>
/// <param name="currentUser">The user of the scope.</param>
/// <param name="currentTenant">The tenant of the scope.</param>
/// <param name="db">The Access context of the scope's tenant.</param>
public sealed class AccessSecurityDataProvider(ICurrentUser currentUser, ICurrentTenant currentTenant, AccessDbContext db)
    : ISecurityDataProvider
{
    /// <inheritdoc />
    public string? GetCurrentUserId() => currentUser.IsAuthenticated ? currentUser.UserId.ToString("D") : null;

    /// <inheritdoc />
    public string? GetCurrentTenantId() => currentTenant.IsSet ? currentTenant.TenantId.ToString("D") : null;

    /// <inheritdoc />
    public async Task<IEnumerable<string>> GetUserRolesAsync(string userId)
    {
        if (!Guid.TryParse(userId, out var id))
        {
            return [];
        }

        return await (from userRole in db.UserRoles
                      join role in db.Roles on userRole.RoleId equals role.Id
                      where userRole.UserId == id && role.Name != null
                      select role.Name!)
            .AsNoTracking()
            .ToListAsync()
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The codes of the permissions the user's roles grant (such as <c>ACCESS.USERS.READ</c>), in one query: only active
    /// permissions of active modules, and none for an inactive user. The engine adds the per-user allows and removes the
    /// denies.
    /// </summary>
    /// <param name="userId">The user's identifier.</param>
    /// <returns>The permission codes, each once.</returns>
    public async Task<IEnumerable<string>> GetUserPermissionsAsync(string userId)
    {
        if (!Guid.TryParse(userId, out var id))
        {
            return [];
        }

        return await (from user in db.Users
                      join userRole in db.UserRoles on user.Id equals userRole.UserId
                      join rolePermission in db.RolePermissions on userRole.RoleId equals rolePermission.RoleId
                      where user.Id == id
                            && user.IsActive
                            && rolePermission.Permission.IsActive
                            && rolePermission.Permission.Module.IsActive
                      select rolePermission.Permission.Code)
            .Distinct()
            .AsNoTracking()
            .ToListAsync()
            .ConfigureAwait(false);
    }
}
