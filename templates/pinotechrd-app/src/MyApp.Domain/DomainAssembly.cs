using System.Reflection;

namespace MyApp.Domain;

/// <summary>Marks the domain assembly for architecture tests and assembly scanning.</summary>
public static class DomainAssembly
{
    /// <summary>Gets the assembly of the domain layer.</summary>
    public static Assembly Assembly { get; } = typeof(DomainAssembly).Assembly;
}
