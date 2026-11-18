using System.Reflection;

namespace MyApp.BuildingBlocks.Web;

/// <summary>Marks the web building-block assembly for architecture tests and assembly scanning.</summary>
public static class BuildingBlocksWebAssembly
{
    /// <summary>Gets the assembly of the web building blocks.</summary>
    public static Assembly Assembly { get; } = typeof(BuildingBlocksWebAssembly).Assembly;
}
