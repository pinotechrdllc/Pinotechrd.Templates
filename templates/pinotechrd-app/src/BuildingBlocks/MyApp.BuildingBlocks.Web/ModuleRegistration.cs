using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MyApp.BuildingBlocks.Web;

/// <summary>Adds business modules (<see cref="IModule"/>) to the host and names their route segment.</summary>
public static class ModuleRegistration
{
    /// <summary>
    /// Adds the module <typeparamref name="TModule"/>: registers it, runs its <see cref="IModule.AddServices"/> with a
    /// <see cref="ModuleContext"/> made of the builder's services, configuration and environment, and registers, keyed by
    /// <see cref="IModule.Name"/>, every public, non-abstract, non-generic <see cref="IEndpoint"/> implementation of its
    /// <see cref="IModule.EndpointsAssembly"/>, in ordinal order of their full names.
    /// <see cref="EndpointRegistration.MapApiEndpoints"/> maps them under <c>/api/v1/{route segment}</c>. A module added
    /// again is ignored, so it is registered and mapped once. Do not also pass a module's endpoints assembly to
    /// <see cref="EndpointRegistration.AddApiEndpoints"/>: its endpoints would be mapped a second time, without the prefix.
    /// </summary>
    /// <typeparam name="TModule">The module to add.</typeparam>
    /// <param name="builder">The builder of the host.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// The module name is empty or not a valid module name (see <see cref="RouteSegmentOf"/>); the message names the
    /// module type.
    /// </exception>
    /// <exception cref="InvalidOperationException">Another module already uses the same route segment.</exception>
    [RequiresUnreferencedCode(
        "Finds endpoints by scanning the module's assembly with reflection; the trimmer may remove them, so this is not trim- or AOT-safe.")]
    public static IHostApplicationBuilder AddModule<TModule>(this IHostApplicationBuilder builder)
        where TModule : class, IModule, new()
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;
        var registered = RegisteredModules(services).ToList();
        if (registered.Exists(m => m.GetType() == typeof(TModule)))
        {
            return builder;
        }

        var module = new TModule();
        var segment = ValidSegmentOf(module);
        if (registered.Find(m => RouteSegmentOf(m.Name) == segment) is { } clash)
        {
            throw new InvalidOperationException(
                $"The modules '{clash.Name}' ({clash.GetType().FullName}) and '{module.Name}' ({typeof(TModule).FullName}) would both be mapped under '{EndpointRegistration.RoutePrefix}/{segment}'.");
        }

        services.AddSingleton<IModule>(module);
        module.AddServices(new ModuleContext(services, builder.Configuration, builder.Environment));
        foreach (var type in EndpointRegistration.EndpointTypes(module.EndpointsAssembly))
        {
            services.TryAddEnumerable(ServiceDescriptor.KeyedSingleton(typeof(IEndpoint), module.Name, type));
        }

        return builder;
    }

    /// <summary>
    /// Converts a module name to its route segment, in kebab case: lower case, with a hyphen where a new word starts.
    /// Module names use ASCII letters and digits only. A word starts at an upper-case letter that follows a lower-case
    /// letter or a digit, and at the last upper-case letter of an acronym when a lower-case letter follows it. Any other
    /// ASCII character (an underscore) becomes one hyphen, and none is left at either end. So <c>Sales</c> gives
    /// <c>sales</c>, <c>SalesOrders</c> <c>sales-orders</c>, <c>HRPayroll</c> <c>hr-payroll</c>, <c>ApiKeys</c> and
    /// <c>APIKeys</c> both <c>api-keys</c>, <c>Sales2Orders</c> <c>sales2-orders</c>, <c>IO</c> <c>io</c> and
    /// <c>Sales_Orders</c> <c>sales-orders</c>.
    /// </summary>
    /// <param name="moduleName">The module name.</param>
    /// <returns>The route segment, without slashes.</returns>
    /// <exception cref="ArgumentException">
    /// The name is empty, has no letter or digit, or has a character outside ASCII (such as <c>Ñandú</c>).
    /// </exception>
    public static string RouteSegmentOf(string moduleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);

        var segment = new StringBuilder(moduleName.Length + 4);
        for (var i = 0; i < moduleName.Length; i++)
        {
            var current = moduleName[i];
            if (!char.IsAscii(current))
            {
                throw new ArgumentException(
                    $"The module name '{moduleName}' has the character '{current}': module names use ASCII letters and digits.",
                    nameof(moduleName));
            }

            if (!char.IsAsciiLetterOrDigit(current))
            {
                AppendHyphen(segment);
                continue;
            }

            if (i > 0 && char.IsAsciiLetterUpper(current) && StartsWord(moduleName[i - 1], i + 1 < moduleName.Length ? moduleName[i + 1] : '\0'))
            {
                AppendHyphen(segment);
            }

            segment.Append(char.ToLowerInvariant(current));
        }

        var result = segment.ToString().TrimEnd('-');
        if (result.Length == 0)
        {
            throw new ArgumentException(
                $"The module name '{moduleName}' has no letter or digit: module names use ASCII letters and digits.", nameof(moduleName));
        }

        return result;
    }

    // The route segment of a module, or an error that names the module type, which a bad name alone would not identify.
    private static string ValidSegmentOf(IModule module)
    {
        var type = module.GetType().FullName;
        if (string.IsNullOrWhiteSpace(module.Name))
        {
            throw new ArgumentException($"The module {type} has no name: its Name is null, empty or white space.");
        }

        try
        {
            return RouteSegmentOf(module.Name);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException($"The module {type} has an invalid name. {exception.Message}", exception);
        }
    }

    private static IEnumerable<IModule> RegisteredModules(IServiceCollection services) =>
        services.Where(d => d.ServiceType == typeof(IModule) && !d.IsKeyedService)
            .Select(d => d.ImplementationInstance)
            .OfType<IModule>();

    // An upper-case letter starts a word after a lower-case letter or a digit (salesOrders, sales2Orders), or ends an
    // acronym when a lower-case letter follows it (the P of HRPayroll).
    private static bool StartsWord(char previous, char next) =>
        char.IsAsciiLetterLower(previous) || char.IsAsciiDigit(previous)
        || (char.IsAsciiLetterUpper(previous) && char.IsAsciiLetterLower(next));

    private static void AppendHyphen(StringBuilder segment)
    {
        if (segment.Length > 0 && segment[^1] != '-')
        {
            segment.Append('-');
        }
    }
}
