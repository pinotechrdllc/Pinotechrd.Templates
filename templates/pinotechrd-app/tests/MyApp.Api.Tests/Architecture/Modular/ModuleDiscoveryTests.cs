namespace MyApp.Api.Tests.Architecture.Modular;

/// <summary>
/// Proves that the run-time discovery finds the module assemblies where they exist, so the rules over them are not
/// checking an empty set, and that every module has exactly its five projects. Exact in each case: a modular project has
/// the module assemblies and no clean layer, the template source (the superset) has both, and nothing else.
/// </summary>
public class ModuleDiscoveryTests
{
    [Fact]
    public void DiscoveryFindsTheFiveProjectsOfTheStampModule()
    {
        var discovered = BusinessAssemblies.Discover();

        BusinessAssemblies.AssertFound(discovered, ModuleAssemblies.Stamp.Select(ReferenceRules.NameOf));
    }

    // Breaks when a module lacks one of its five projects, has a project of another name, or a project name is malformed.
    [Fact]
    public void EveryModuleHasExactlyTheFiveProjectsOfAModule()
    {
        var names = ModuleAssemblies.DiscoveredNames();

        var malformed = names.Where(n => ModuleAssemblyNames.Parse(n, ReferenceRules.SolutionRoot) is null).ToList();
        Assert.True(malformed.Count == 0, $"Not named {{Root}}.Modules.{{Module}}.{{Layer}}: {string.Join(", ", malformed)}.");

        var modules = names
            .Select(n => ModuleAssemblyNames.Parse(n, ReferenceRules.SolutionRoot)!)
            .GroupBy(n => n.Module, StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(modules);
        foreach (var module in modules)
        {
            Assert.Equal(
                ModuleAssemblyNames.Layers.Order(StringComparer.Ordinal),
                module.Select(n => n.Layer).Order(StringComparer.Ordinal));
        }
    }

    // The exact count of the whole business set: in a generated modular project it is the module assemblies and nothing
    // else (no clean layer survives generation); in the template source it is those plus the clean layers, which the
    // clean rules count.
    [Fact]
    public void AModularProjectHasNoBusinessAssemblyOutsideItsModules()
    {
        var outside = BusinessAssemblies.Discover()
            .Where(n => !ModuleAssemblyNames.IsModuleAssembly(n, ReferenceRules.SolutionRoot))
            .ToList();

        if (ArchitectureLayout.IsSuperset())
        {
            Assert.NotEmpty(outside);
        }
        else
        {
            Assert.Empty(outside);
        }
    }

    [Fact]
    public void TheModuleAssembliesAreNeitherABuildingBlockTheHostNorATestAssembly()
    {
        var names = ModuleAssemblies.DiscoveredNames();

        Assert.DoesNotContain(names, n => n.EndsWith(".Tests", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => ReferenceRules.HasPrefix(n, ReferenceRules.SolutionRoot + ".BuildingBlocks"));
        Assert.DoesNotContain(names, n => n == ReferenceRules.NameOf(ReferenceRules.Api));
    }
}
