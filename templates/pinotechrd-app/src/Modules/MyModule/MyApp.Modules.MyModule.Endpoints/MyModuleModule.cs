using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MyApp.BuildingBlocks.Application;
using MyApp.BuildingBlocks.Web;
using MyApp.Modules.MyModule.Application;

namespace MyApp.Modules.MyModule.Endpoints;

/// <summary>
/// The MyModule module as the host sees it, and its composition root: it registers the services of every project of
/// the module. Its endpoints are the <see cref="IEndpoint"/> implementations of this project, mapped under
/// <c>/api/v1/{route segment of the name}</c>.
/// </summary>
public sealed class MyModuleModule : IModule
{
    /// <inheritdoc />
    public string Name => "MyModule";

    /// <inheritdoc />
    public Assembly EndpointsAssembly => MyModuleEndpointsAssembly.Assembly;

    /// <inheritdoc />
    public void AddServices(ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Services.AddHandlers(MyModuleApplicationAssembly.Assembly);

        // The built-in validation is generated at compile time only for the request types of the endpoints mapped in the
        // project that makes this call. It must stay in this project, the one that maps the module's endpoints: called
        // from the host or another module, this module's request bodies would not be validated. Keep it the only call
        // in this project: a second call site fails the build (CS8785).
        context.Services.AddValidation();
    }
}
