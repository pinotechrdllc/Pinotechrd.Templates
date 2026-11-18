using MyApp.BuildingBlocks.Web;
using MyApp.Modules.MyModule.Endpoints;

namespace MyApp.Api;

// Only a modular application keeps this file. Each module is listed here once; adding a module adds one line.
public static partial class BusinessComposition
{
    static partial void AddModules(IHostApplicationBuilder builder)
    {
        builder.AddModule<MyModuleModule>();
    }
}
