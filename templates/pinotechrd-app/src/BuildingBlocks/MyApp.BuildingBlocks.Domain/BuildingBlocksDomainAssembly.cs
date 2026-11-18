using System.Reflection;

namespace MyApp.BuildingBlocks.Domain;

/// <summary>Marks the domain building-block assembly for architecture tests and assembly scanning.</summary>
public static class BuildingBlocksDomainAssembly
{
    /// <summary>Gets the assembly of the domain building blocks.</summary>
    public static Assembly Assembly { get; } = typeof(BuildingBlocksDomainAssembly).Assembly;
}
