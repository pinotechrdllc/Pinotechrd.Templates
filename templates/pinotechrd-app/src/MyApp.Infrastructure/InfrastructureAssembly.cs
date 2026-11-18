using System.Reflection;

namespace MyApp.Infrastructure;

/// <summary>Marks the infrastructure assembly for architecture tests and assembly scanning.</summary>
public static class InfrastructureAssembly
{
    /// <summary>Gets the assembly of the infrastructure layer.</summary>
    public static Assembly Assembly { get; } = typeof(InfrastructureAssembly).Assembly;
}
