using System.Reflection;

namespace MyApp.Modules.MyModule.Application;

/// <summary>Marks the application assembly of the MyModule module for architecture tests and assembly scanning.</summary>
public static class MyModuleApplicationAssembly
{
    /// <summary>Gets the application assembly of the MyModule module.</summary>
    public static Assembly Assembly { get; } = typeof(MyModuleApplicationAssembly).Assembly;
}
