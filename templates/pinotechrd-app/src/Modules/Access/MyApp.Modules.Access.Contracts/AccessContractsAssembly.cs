using System.Reflection;

namespace MyApp.Modules.Access.Contracts;

/// <summary>Marks the contracts assembly of the Access module for architecture tests and assembly scanning.</summary>
public static class AccessContractsAssembly
{
    /// <summary>Gets the contracts assembly of the Access module.</summary>
    public static Assembly Assembly { get; } = typeof(AccessContractsAssembly).Assembly;
}
