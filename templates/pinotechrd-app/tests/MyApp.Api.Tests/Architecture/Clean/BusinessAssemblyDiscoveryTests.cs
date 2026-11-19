namespace MyApp.Api.Tests.Architecture.Clean;

/// <summary>
/// Proves that the run-time discovery of business assemblies finds something where something exists: in a clean project it
/// must find exactly the five layers, so the shared rules over business assemblies are not checking an empty set. In the
/// template source (the superset) the module assemblies of the stamp are there too; they are counted by the modular rules,
/// and here nothing but them may come on top of the five layers.
/// </summary>
public class BusinessAssemblyDiscoveryTests
{
    [Fact]
    public void DiscoveryFindsTheFiveLayerAssembliesOfACleanProject()
    {
        var discovered = BusinessAssemblies.Discover();
        var layers = discovered.Where(n => !ModuleAssemblyNames.IsModuleAssembly(n, ReferenceRules.SolutionRoot)).ToList();

        BusinessAssemblies.AssertFound(discovered, CleanLayers.All.Select(ReferenceRules.NameOf));
        Assert.Equal(
            CleanLayers.All.Select(ReferenceRules.NameOf).OrderBy(n => n, StringComparer.Ordinal),
            layers);
    }

    // The Access module ships with every project, so a clean project has module assemblies: exactly the five of Access.
    // The template source (the superset) also has the stamp module, which only the modular rules count.
    [Fact]
    public void ACleanProjectHasNoModuleAssemblyButTheFiveOfAccess()
    {
        var modules = BusinessAssemblies.Discover()
            .Where(n => ModuleAssemblyNames.IsModuleAssembly(n, ReferenceRules.SolutionRoot))
            .ToList();
        var access = ModuleAssemblyNames.Layers.Select(l => $"{ReferenceRules.SolutionRoot}.Modules.Access.{l}").Order(StringComparer.Ordinal).ToList();

        if (ArchitectureLayout.IsSuperset())
        {
            Assert.Equal(access, modules.Where(n => n.StartsWith($"{ReferenceRules.SolutionRoot}.Modules.Access.", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        }
        else
        {
            Assert.Equal(access, modules.Order(StringComparer.Ordinal));
        }
    }
}
