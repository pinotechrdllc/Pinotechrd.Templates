using Microsoft.Extensions.DependencyInjection;
using MyApp.BuildingBlocks.Application.Security;

namespace MyApp.Modules.Access.Application.Security;

/// <summary>
/// The permissions of the Access module. The migrator seeds them into every tenant (codes in upper case, such as
/// <c>ACCESS.USERS.READ</c>), and the module's endpoints require them.
/// </summary>
public static class AccessPermissions
{
    /// <summary>Lists the tenant's users.</summary>
    public const string UsersRead = "Access.Users.Read";

    /// <summary>Creates, changes and deactivates the tenant's users.</summary>
    public const string UsersManage = "Access.Users.Manage";

    /// <summary>Lists the tenant's roles and their permissions.</summary>
    public const string RolesRead = "Access.Roles.Read";

    /// <summary>Creates and changes the tenant's roles and their permissions.</summary>
    public const string RolesManage = "Access.Roles.Manage";

    /// <summary>Gets every permission of the module.</summary>
    public static IReadOnlyList<string> All { get; } = [UsersRead, UsersManage, RolesRead, RolesManage];

    /// <summary>
    /// Adds the module's permissions to the <see cref="PermissionCatalogue"/>. Called by the module (the API) and by the
    /// migrator, which seeds the catalogue. A second call has no further effect: the catalogue keeps each name once.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddAccessPermissions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddPermissions([.. All]);
    }
}
