using MyApp.Modules.MyModule.Domain;

namespace MyApp.Api;

public static partial class Startup
{
    static partial void AddModules(List<string> parts) => parts.Add(MyModuleModule.Name);
}
