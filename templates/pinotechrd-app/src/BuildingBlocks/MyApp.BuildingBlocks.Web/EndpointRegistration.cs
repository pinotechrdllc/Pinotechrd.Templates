using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MyApp.BuildingBlocks.Web;

/// <summary>Discovers the <see cref="IEndpoint"/> implementations of assemblies and maps them under one versioned group.</summary>
public static class EndpointRegistration
{
    /// <summary>The route prefix of every API endpoint.</summary>
    public const string RoutePrefix = "/api/v1";

    /// <summary>
    /// Registers as singletons every public, non-abstract, non-generic <see cref="IEndpoint"/> implementation of
    /// <paramref name="assemblies"/>, in ordinal order of their full names. An implementation found more than once, in
    /// a repeated assembly or a repeated call, is registered once. These are the host's own endpoints; a module's
    /// endpoints are registered by <see cref="ModuleRegistration.AddModule{TModule}"/> instead.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="assemblies">The assemblies to scan.</param>
    /// <returns>The same service collection, for chaining.</returns>
    [RequiresUnreferencedCode(
        "Finds endpoints by scanning the assemblies with reflection; the trimmer may remove them, so this is not trim- or AOT-safe.")]
    public static IServiceCollection AddApiEndpoints(this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        foreach (var type in EndpointTypes(assemblies))
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IEndpoint), type));
        }

        return services;
    }

    /// <summary>
    /// Maps every registered <see cref="IEndpoint"/> under <see cref="RoutePrefix"/>: first the host's endpoints, then
    /// the endpoints of each module added with <see cref="ModuleRegistration.AddModule{TModule}"/>, by module name in
    /// ordinal order, in a group <c>{RoutePrefix}/{route segment}</c> (see <see cref="ModuleRegistration.RouteSegmentOf"/>)
    /// tagged with the module name; an endpoint that sets its own tags with <c>WithTags</c> replaces that tag. Within
    /// each, endpoints keep the ordinal order of their full type names, so the order of the API document only changes
    /// when the endpoints do. A module added twice is mapped once.
    /// <para>
    /// When modules are registered, a module whose route segment equals the first literal segment of a host endpoint's
    /// route (a module <c>Version</c> next to <c>GET /api/v1/version</c>) stops the start with an
    /// <see cref="InvalidOperationException"/> naming the module. To read those routes the host's endpoints are built
    /// here, so no convention can be added to a single host endpoint after this call; conventions added to the returned
    /// group still apply.
    /// </para>
    /// </summary>
    /// <param name="routes">The application route builder.</param>
    /// <returns>The versioned group, to add conventions shared by every endpoint.</returns>
    /// <exception cref="InvalidOperationException">A module's route segment is taken by a host endpoint.</exception>
    public static RouteGroupBuilder MapApiEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var services = routes.ServiceProvider;
        var group = routes.MapGroup(RoutePrefix);
        foreach (var endpoint in services.GetServices<IEndpoint>())
        {
            endpoint.MapEndpoint(group);
        }

        var modules = services.GetServices<IModule>().OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
        if (modules.Count == 0)
        {
            return group;
        }

        // Read before any module group is added to the versioned group, so only the host's endpoints are built.
        var hostSegments = FirstLiteralSegments(group);
        foreach (var module in modules)
        {
            var segment = ModuleRegistration.RouteSegmentOf(module.Name);
            if (hostSegments.Contains(segment))
            {
                throw new InvalidOperationException(
                    $"The module '{module.Name}' ({module.GetType().FullName}) would be mapped under '{RoutePrefix}/{segment}', where the host already maps an endpoint. Rename the module or the host endpoint.");
            }

            var moduleGroup = group.MapGroup("/" + segment).WithTags(module.Name);
            foreach (var endpoint in services.GetKeyedServices<IEndpoint>(module.Name))
            {
                endpoint.MapEndpoint(moduleGroup);
            }
        }

        return group;
    }

    // The first segment of each route mapped in the group, when it is plain text (a route parameter matches any segment
    // and loses to a literal one, so it takes no module's place). Routing ignores case, so the set does too.
    private static HashSet<string> FirstLiteralSegments(IEndpointRouteBuilder group) =>
        group.DataSources
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.PathSegments)
            .Where(s => s.Count > 0 && s[0].Parts is [RoutePatternLiteralPart])
            .Select(s => ((RoutePatternLiteralPart)s[0].Parts[0]).Content)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The public, non-abstract, non-generic <see cref="IEndpoint"/> implementations of <paramref name="assemblies"/>,
    /// each once, ordered by full name. Not by <c>GetTypes()</c>, which follows compile order: the mapping order, and
    /// with it the order of the API document, then only changes when the endpoints do.
    /// </summary>
    [RequiresUnreferencedCode("Scans the assemblies with reflection.")]
    internal static IEnumerable<Type> EndpointTypes(params Assembly[] assemblies) =>
        assemblies.Distinct().SelectMany(a => a.GetTypes()).Where(IsEndpoint).OrderBy(t => t.FullName, StringComparer.Ordinal);

    private static bool IsEndpoint(Type type) =>
        type is { IsClass: true, IsAbstract: false, IsVisible: true, ContainsGenericParameters: false }
        && typeof(IEndpoint).IsAssignableFrom(type);
}
