using System.Reflection;

namespace MyApp.Application;

/// <summary>Marks the application assembly for architecture tests and assembly scanning.</summary>
public static class ApplicationAssembly
{
    /// <summary>Gets the assembly of the application layer.</summary>
    public static Assembly Assembly { get; } = typeof(ApplicationAssembly).Assembly;
}
