using System.Reflection;

namespace MyApp.Api;

/// <summary>Marks the API assembly for architecture tests and endpoint discovery.</summary>
public static class ApiAssembly
{
    /// <summary>Gets the assembly of the API layer.</summary>
    public static Assembly Assembly { get; } = typeof(ApiAssembly).Assembly;
}
