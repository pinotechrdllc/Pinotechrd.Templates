namespace MyApp.Api.Tests.Architecture.Clean;

/// <summary>
/// Proves that the run-time discovery of business assemblies finds something where something exists: in a clean project it
/// must find exactly the five layers, so the shared rules over business assemblies are not checking an empty set.
/// </summary>
public class BusinessAssemblyDiscoveryTests
{
    [Fact]
    public void DiscoveryFindsTheFiveLayerAssembliesOfACleanProject()
    {
        var discovered = BusinessAssemblies.Discover();

        BusinessAssemblies.AssertFound(discovered, CleanLayers.All.Select(ReferenceRules.NameOf));
        Assert.Equal(
            CleanLayers.All.Select(ReferenceRules.NameOf).OrderBy(n => n, StringComparer.Ordinal),
            discovered);
    }
}
