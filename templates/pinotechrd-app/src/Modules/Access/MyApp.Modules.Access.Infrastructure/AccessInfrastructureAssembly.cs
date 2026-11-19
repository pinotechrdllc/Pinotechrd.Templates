using System.Reflection;

namespace MyApp.Modules.Access.Infrastructure;

/// <summary>Marks the infrastructure assembly of the Access module for architecture tests and assembly scanning.</summary>
public static class AccessInfrastructureAssembly
{
    /// <summary>Gets the infrastructure assembly of the Access module.</summary>
    public static Assembly Assembly { get; } = typeof(AccessInfrastructureAssembly).Assembly;
}
