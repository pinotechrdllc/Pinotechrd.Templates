using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MyApp.BuildingBlocks.Application.Security;

/// <summary>Registers the permissions a module, or the host, declares, in the <see cref="PermissionCatalogue"/>.</summary>
public static class PermissionRegistration
{
    /// <summary>
    /// Adds <paramref name="permissions"/> to the <see cref="PermissionCatalogue"/> (a singleton built from every call).
    /// A module calls it from its <c>AddServices</c> with the permissions its endpoints require. Calling it with no
    /// permission only registers the catalogue.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="permissions">The permissions, named as <see cref="PermissionName"/> requires.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentException">A name is not a valid permission name; the message quotes it.</exception>
    public static IServiceCollection AddPermissions(this IServiceCollection services, params string[] permissions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(permissions);

        foreach (var permission in permissions)
        {
            PermissionName.Validate(permission, nameof(permissions));
        }

        if (permissions.Length > 0)
        {
            services.AddSingleton(new DeclaredPermissions([.. permissions]));
        }

        services.TryAddSingleton(provider =>
            new PermissionCatalogue(provider.GetServices<DeclaredPermissions>().SelectMany(d => d.Names)));
        AddCheckerRouting(services);
        return services;
    }

    /// <summary>
    /// Registers <typeparamref name="TChecker"/> (scoped) as the <see cref="IPermissionChecker"/> of the permissions of
    /// <paramref name="module"/>, those whose first segment is that name: <c>AddPermissionChecker&lt;AccessChecker&gt;("Access")</c>
    /// answers <c>Access.Users.Read</c>. The application asks <see cref="PermissionCheckerRouter"/>, which sends each
    /// permission to the checker of its module and denies the permissions of a module without one. Two checkers for one
    /// module (compared ignoring case), or a checker registered as <see cref="IPermissionChecker"/> itself, stop the host
    /// at start.
    /// </summary>
    /// <typeparam name="TChecker">The checker.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="module">The module name, a PascalCase name of ASCII letters and digits such as <c>Access</c>.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="module"/> is not a valid module name.</exception>
    public static IServiceCollection AddPermissionChecker<TChecker>(this IServiceCollection services, string module)
        where TChecker : class, IPermissionChecker
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!PermissionName.IsValidModule(module))
        {
            throw new ArgumentException(
                $"The module '{module}' is not valid: a module is the first segment of a permission, a PascalCase name of ASCII letters and digits such as 'Access'.",
                nameof(module));
        }

        services.AddKeyedScoped<IPermissionChecker, TChecker>(module);
        AddCheckerRouting(services);
        return services;
    }

    // The router, the map of owners (read from this collection when first resolved, so it sees every registration) and the
    // one non-keyed IPermissionChecker, which is the router.
    private static void AddCheckerRouting(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(PermissionCheckerMap)))
        {
            return;
        }

        var router = ServiceDescriptor.Scoped<IPermissionChecker>(provider => provider.GetRequiredService<PermissionCheckerRouter>());
        services.AddSingleton(_ => new PermissionCheckerMap(services, router));
        services.TryAddScoped<PermissionCheckerRouter>();
        services.Add(router);
    }

    // One call's permissions; the catalogue reads them all.
    private sealed record DeclaredPermissions(IReadOnlyList<string> Names);
}
