using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MyApp.BuildingBlocks.Application;

/// <summary>Registers the use-case handlers of an assembly.</summary>
public static class HandlerRegistration
{
    private const string HandlerSuffix = "Handler";
    private const string HandleMethodName = "HandleAsync";

    /// <summary>
    /// Registers as scoped, under its own type, every public or internal concrete class of
    /// <paramref name="assembly"/> whose name ends in <c>Handler</c> and that has a public instance method named
    /// <c>HandleAsync</c>, declared or inherited. Framework handlers that follow another shape, such as
    /// authorization or HTTP message handlers, are not matched. A handler already registered is left as is.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="assembly">The assembly to scan.</param>
    /// <returns>The same service collection, for chaining.</returns>
    [RequiresUnreferencedCode(
        "Finds handlers by scanning the assembly with reflection; the trimmer may remove them, so this is not trim- or AOT-safe.")]
    public static IServiceCollection AddHandlers(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assembly);

        foreach (var type in assembly.GetTypes().Where(IsHandler))
        {
            services.TryAddScoped(type);
        }

        return services;
    }

    private static bool IsHandler(Type type) =>
        type is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false }
        && (type.IsPublic || type.IsNotPublic || type.IsNestedPublic || type.IsNestedAssembly)
        && !type.IsSubclassOf(typeof(Delegate))
        && type.Name.EndsWith(HandlerSuffix, StringComparison.Ordinal)
        && Array.Exists(
            type.GetMethods(BindingFlags.Public | BindingFlags.Instance),
            m => m.Name == HandleMethodName);
}
