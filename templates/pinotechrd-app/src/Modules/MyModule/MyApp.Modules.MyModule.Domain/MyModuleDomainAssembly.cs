using System.Reflection;

namespace MyApp.Modules.MyModule.Domain;

/// <summary>Marks the domain assembly of the MyModule module for architecture tests and assembly scanning.</summary>
public static class MyModuleDomainAssembly
{
    /// <summary>Gets the domain assembly of the MyModule module.</summary>
    public static Assembly Assembly { get; } = typeof(MyModuleDomainAssembly).Assembly;
}
