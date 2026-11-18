using System.Reflection;

namespace MyApp.Resources;

/// <summary>Marks the resources assembly for architecture tests and assembly scanning.</summary>
public static class ResourcesAssembly
{
    /// <summary>Gets the assembly of the resources layer.</summary>
    public static Assembly Assembly { get; } = typeof(ResourcesAssembly).Assembly;
}
