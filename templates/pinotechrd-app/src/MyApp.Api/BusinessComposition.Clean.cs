using MyApp.Application;
using MyApp.BuildingBlocks.Application;

namespace MyApp.Api;

// Only a clean application keeps this file. It registers the services of the layers; a modular application registers
// them in each module instead.
public static partial class BusinessComposition
{
    static partial void AddLayers(IHostApplicationBuilder builder)
    {
        // Every *Handler class of the application layer (the use cases), so an endpoint can inject it.
        builder.Services.AddHandlers(ApplicationAssembly.Assembly);
    }
}
