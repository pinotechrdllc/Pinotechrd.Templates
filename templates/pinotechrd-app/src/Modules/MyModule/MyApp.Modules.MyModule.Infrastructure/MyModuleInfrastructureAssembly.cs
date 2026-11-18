using System.Reflection;

namespace MyApp.Modules.MyModule.Infrastructure;

/// <summary>Marks the infrastructure assembly of the MyModule module for architecture tests and assembly scanning.</summary>
public static class MyModuleInfrastructureAssembly
{
    /// <summary>Gets the infrastructure assembly of the MyModule module.</summary>
    public static Assembly Assembly { get; } = typeof(MyModuleInfrastructureAssembly).Assembly;
}
