using System.Reflection;

namespace MyApp.Modules.Access.Endpoints;

/// <summary>Marks the endpoints assembly of the Access module for architecture tests and assembly scanning.</summary>
public static class AccessEndpointsAssembly
{
    /// <summary>Gets the endpoints assembly of the Access module.</summary>
    public static Assembly Assembly { get; } = typeof(AccessEndpointsAssembly).Assembly;
}
