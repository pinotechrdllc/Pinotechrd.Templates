using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MyApp.BuildingBlocks.Application.Security;

namespace MyApp.BuildingBlocks.Web.Authorization;

/// <summary>
/// Endpoint metadata of an endpoint that requires a permission. Only <see cref="EndpointAccess.RequirePermission{TBuilder}"/>
/// creates it, together with the authorization requirement that enforces it; the start-time check refuses an endpoint
/// where the two do not match.
/// </summary>
public sealed class PermissionMetadata
{
    internal PermissionMetadata(string permission) => Permission = permission;

    /// <summary>Gets the permission, a valid <see cref="PermissionName"/>.</summary>
    public string Permission { get; }
}

/// <summary>
/// Endpoint metadata of an endpoint that any authenticated user may call. Only
/// <see cref="EndpointAccess.AuthenticatedOnly{TBuilder}"/> creates it, together with <c>RequireAuthorization()</c>.
/// </summary>
public sealed class AuthenticatedOnlyMetadata
{
    /// <summary>The one instance; the marker carries no data.</summary>
    internal static AuthenticatedOnlyMetadata Instance { get; } = new();

    private AuthenticatedOnlyMetadata()
    {
    }
}

/// <summary>
/// The three ways an endpoint declares its access. Every routed endpoint except the health probes and the OpenAPI
/// document declares exactly one, on the endpoint itself: <see cref="RequirePermission{TBuilder}"/>,
/// <see cref="AuthenticatedOnly{TBuilder}"/> or the platform's <c>AllowAnonymous()</c>. The host refuses to start
/// otherwise (see <see cref="ApiAuthorizationRegistration.AddApiAuthorization"/>). The first two refuse a route group;
/// <c>AllowAnonymous()</c> on a group cannot be told apart from one on each endpoint, so it is not used on groups.
/// </summary>
public static class EndpointAccess
{
    /// <summary>
    /// Requires <paramref name="permission"/>: the caller must be authenticated (otherwise 401 <c>unauthorized</c>) and
    /// <see cref="IPermissionChecker"/> must grant it the permission (otherwise 403 <c>forbidden</c>). The permission
    /// must be registered with <see cref="PermissionRegistration.AddPermissions"/>, or the host does not start.
    /// </summary>
    /// <typeparam name="TBuilder">The type of the endpoint convention builder.</typeparam>
    /// <param name="builder">The endpoint convention builder.</param>
    /// <param name="permission">The permission, <c>{Module}.{Resource}.{Action}</c> such as <c>Access.Users.Read</c>.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// The permission is not a valid <see cref="PermissionName"/>, or <paramref name="builder"/> is a route group.
    /// </exception>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        RefuseGroup(builder, $"RequirePermission(\"{permission}\")");
        PermissionName.Validate(permission, nameof(permission));

        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(permission))
            .Build();
        builder.RequireAuthorization(policy);
        builder.WithMetadata(new PermissionMetadata(permission));
        return builder;
    }

    /// <summary>
    /// Declares that any authenticated user may call the endpoint: an anonymous caller gets 401 <c>unauthorized</c>, and
    /// no permission is checked. For operations on the caller's own data, such as reading its profile.
    /// </summary>
    /// <typeparam name="TBuilder">The type of the endpoint convention builder.</typeparam>
    /// <param name="builder">The endpoint convention builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="builder"/> is a route group.</exception>
    public static TBuilder AuthenticatedOnly<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        RefuseGroup(builder, "AuthenticatedOnly()");

        builder.RequireAuthorization();
        builder.WithMetadata(AuthenticatedOnlyMetadata.Instance);
        return builder;
    }

    // Access is declared on each endpoint, so adding an endpoint to a group never gives it an access it does not state.
    private static void RefuseGroup(IEndpointConventionBuilder builder, string declaration)
    {
        if (builder is RouteGroupBuilder)
        {
            throw new ArgumentException(
                $"{declaration} is declared on each endpoint, not on a route group: an endpoint added to the group later would inherit it unseen.",
                nameof(builder));
        }
    }

    /// <summary>Whether the endpoint allows anonymous callers (it carries <c>AllowAnonymous()</c>).</summary>
    internal static bool AllowsAnonymous(Endpoint? endpoint) => endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null;
}
