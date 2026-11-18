using System.Reflection;

namespace MyApp.BuildingBlocks.Web;

/// <summary>
/// One business module of a modular application, as the host sees it: a name, the registration of its services and the
/// assembly that holds its endpoints. The implementation lives in the module's endpoints project, the module's
/// composition root, and the host adds it with <see cref="ModuleRegistration.AddModule{TModule}"/>.
/// </summary>
public interface IModule
{
    /// <summary>
    /// Gets the name of the module, a PascalCase C# identifier of ASCII letters and digits such as <c>Sales</c>. Its
    /// endpoints are mapped under <c>/api/v1/{name in kebab case}</c> (see <see cref="ModuleRegistration.RouteSegmentOf"/>)
    /// and tagged with it.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the assembly scanned for the module's <see cref="IEndpoint"/> implementations: its endpoints project.
    /// </summary>
    Assembly EndpointsAssembly { get; }

    /// <summary>
    /// Registers the services of the module in <see cref="ModuleContext.Services"/>. It runs once, when the host adds the
    /// module. It must not read a required configuration value or connect to anything while registering: the build-time
    /// contract generation starts the host with no configuration and no database.
    /// </summary>
    /// <param name="context">The host's services, configuration and environment.</param>
    void AddServices(ModuleContext context);
}
