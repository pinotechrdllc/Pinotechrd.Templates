namespace MyApp.Api;

// Shared by both layouts. The partial methods have no body here. Each layout ships one file
// that implements its method; the call to the method that has no body compiles away.
public static partial class Startup
{
    public static string Describe()
    {
        var parts = new List<string> { "api" };
        AddLayers(parts);
        AddModules(parts);
        return string.Join(",", parts);
    }

    static partial void AddLayers(List<string> parts);

    static partial void AddModules(List<string> parts);
}
