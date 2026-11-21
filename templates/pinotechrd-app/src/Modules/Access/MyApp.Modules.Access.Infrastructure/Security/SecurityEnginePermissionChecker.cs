using MyApp.BuildingBlocks.Application.Identity;
using MyApp.BuildingBlocks.Application.Security;
using Pinotechrd.Security.Interfaces;

namespace MyApp.Modules.Access.Infrastructure.Security;

/// <summary>
/// The permission checker of the module <c>Access</c>: it asks the security library's engine, which grants a permission
/// when the tenant's subscription is active, the module is in its plan, and the user's roles plus the per-user allows,
/// minus the denies, hold it. <c>Access.Users.Read</c> is asked as module <c>Access</c>, permission <c>Users.Read</c>
/// (the library compares codes in upper case, <c>ACCESS.USERS.READ</c>).
/// </summary>
/// <param name="engine">The security engine of the request.</param>
public sealed class SecurityEnginePermissionChecker(ISecurityEngine engine) : IPermissionChecker
{
    /// <inheritdoc />
    public async ValueTask<bool> HasPermissionAsync(ICurrentUser user, string permission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!user.IsAuthenticated || !PermissionName.IsValid(permission))
        {
            return false;
        }

        var dot = permission.IndexOf('.', StringComparison.Ordinal);
        return await engine.CanAccessAsync(permission[..dot], permission[(dot + 1)..], cancellationToken).ConfigureAwait(false);
    }
}
