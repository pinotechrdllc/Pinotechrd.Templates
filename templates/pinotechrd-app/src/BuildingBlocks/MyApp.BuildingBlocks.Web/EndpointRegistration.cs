using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
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
    /// <paramref name="assemblies"/>. An implementation found more than once, in a repeated assembly or a repeated
    /// call, is registered once.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="assemblies">The assemblies to scan, for example one per module.</param>
    /// <returns>The same service collection, for chaining.</returns>
    [RequiresUnreferencedCode(
        "Finds endpoints by scanning the assemblies with reflection; the trimmer may remove them, so this is not trim- or AOT-safe.")]
    public static IServiceCollection AddApiEndpoints(this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        foreach (var type in assemblies.Distinct().SelectMany(a => a.GetTypes()).Where(IsEndpoint))
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IEndpoint), type));
        }

        return services;
    }

    /// <summary>Maps every registered <see cref="IEndpoint"/> under <see cref="RoutePrefix"/>.</summary>
    /// <param name="routes">The application route builder.</param>
    /// <returns>The versioned group, to add conventions shared by every endpoint.</returns>
    public static RouteGroupBuilder MapApiEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup(RoutePrefix);
        foreach (var endpoint in routes.ServiceProvider.GetServices<IEndpoint>())
        {
            endpoint.MapEndpoint(group);
        }

        return group;
    }

    private static bool IsEndpoint(Type type) =>
        type is { IsClass: true, IsAbstract: false, IsVisible: true, ContainsGenericParameters: false }
        && typeof(IEndpoint).IsAssignableFrom(type);
}
