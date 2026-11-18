using System.Reflection;

namespace MyApp.Modules.MyModule.Contracts;

/// <summary>Marks the contracts assembly of the MyModule module for architecture tests and assembly scanning.</summary>
public static class MyModuleContractsAssembly
{
    /// <summary>Gets the contracts assembly of the MyModule module.</summary>
    public static Assembly Assembly { get; } = typeof(MyModuleContractsAssembly).Assembly;
}
