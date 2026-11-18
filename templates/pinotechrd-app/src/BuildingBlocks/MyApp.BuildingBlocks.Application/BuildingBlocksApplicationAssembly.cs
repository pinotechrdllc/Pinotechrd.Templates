using System.Reflection;

namespace MyApp.BuildingBlocks.Application;

/// <summary>Marks the application building-block assembly for architecture tests and assembly scanning.</summary>
public static class BuildingBlocksApplicationAssembly
{
    /// <summary>Gets the assembly of the application building blocks.</summary>
    public static Assembly Assembly { get; } = typeof(BuildingBlocksApplicationAssembly).Assembly;
}
