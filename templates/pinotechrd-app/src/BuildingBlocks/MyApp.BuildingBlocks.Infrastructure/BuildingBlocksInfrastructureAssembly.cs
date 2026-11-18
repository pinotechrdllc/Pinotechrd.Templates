using System.Reflection;

namespace MyApp.BuildingBlocks.Infrastructure;

/// <summary>Marks the infrastructure building-block assembly for architecture tests and assembly scanning.</summary>
public static class BuildingBlocksInfrastructureAssembly
{
    /// <summary>Gets the assembly of the infrastructure building blocks.</summary>
    public static Assembly Assembly { get; } = typeof(BuildingBlocksInfrastructureAssembly).Assembly;
}
