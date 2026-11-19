using MyApp.BuildingBlocks.Application.Identity;

namespace MyApp.BuildingBlocks.Application.Security;

/// <summary>
/// Decides whether a user holds a permission. Each module that owns permissions registers one for its own, with
/// <c>services.AddPermissionChecker&lt;MyChecker&gt;("Module")</c> (see <see cref="PermissionRegistration"/>); the
/// application asks <see cref="PermissionCheckerRouter"/>, which sends each permission to the checker of its module
/// and denies the permissions of a module without one. So until a module registers a checker (the Access module, with
/// the security library) every permission-protected endpoint answers 403 to an authenticated user. Never register a
/// checker as <c>IPermissionChecker</c> itself: the host refuses to start.
/// </summary>
public interface IPermissionChecker
{
    /// <summary>Returns whether <paramref name="user"/> holds <paramref name="permission"/>.</summary>
    /// <param name="user">The current user; it is authenticated when the web building block asks.</param>
    /// <param name="permission">
    /// The permission, exactly as the endpoint declared it and as it is registered in the
    /// <see cref="PermissionCatalogue"/>: <c>{Module}.{Resource}.{Action}</c>, such as <c>Access.Users.Read</c>.
    /// </param>
    /// <param name="cancellationToken">Cancelled when the request is aborted.</param>
    /// <returns><see langword="true"/> when the user holds the permission; otherwise <see langword="false"/>.</returns>
    ValueTask<bool> HasPermissionAsync(ICurrentUser user, string permission, CancellationToken cancellationToken);
}
