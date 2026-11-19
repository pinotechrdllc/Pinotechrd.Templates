using System.Reflection;

namespace MyApp.Modules.Access.Application;

/// <summary>Marks the application assembly of the Access module for architecture tests and assembly scanning.</summary>
public static class AccessApplicationAssembly
{
    /// <summary>Gets the application assembly of the Access module.</summary>
    public static Assembly Assembly { get; } = typeof(AccessApplicationAssembly).Assembly;
}
