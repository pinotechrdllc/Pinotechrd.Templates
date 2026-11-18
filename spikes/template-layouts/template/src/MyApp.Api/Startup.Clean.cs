using MyApp.Application;

namespace MyApp.Api;

public static partial class Startup
{
    static partial void AddLayers(List<string> parts) => parts.Add(ApplicationMarker.Layer);
}
