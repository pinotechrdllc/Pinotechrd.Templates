using System.Reflection;

namespace MyApp.Contracts;

/// <summary>Marks the contracts assembly for architecture tests and assembly scanning.</summary>
public static class ContractsAssembly
{
    /// <summary>Gets the assembly of the contracts layer.</summary>
    public static Assembly Assembly { get; } = typeof(ContractsAssembly).Assembly;
}
