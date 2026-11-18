using System.Reflection;

namespace MyApp.Modules.MyModule.Endpoints;

/// <summary>Marks the endpoints assembly of the MyModule module for architecture tests and assembly scanning.</summary>
public static class MyModuleEndpointsAssembly
{
    /// <summary>Gets the endpoints assembly of the MyModule module.</summary>
    public static Assembly Assembly { get; } = typeof(MyModuleEndpointsAssembly).Assembly;
}
