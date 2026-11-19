using System.Reflection;

namespace MyApp.Modules.Access.Domain;

/// <summary>Marks the domain assembly of the Access module for architecture tests and assembly scanning.</summary>
public static class AccessDomainAssembly
{
    /// <summary>Gets the domain assembly of the Access module.</summary>
    public static Assembly Assembly { get; } = typeof(AccessDomainAssembly).Assembly;
}
