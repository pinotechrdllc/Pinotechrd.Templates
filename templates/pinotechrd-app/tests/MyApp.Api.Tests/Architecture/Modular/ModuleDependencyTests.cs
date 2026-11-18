using System.Reflection;
using System.Xml.Linq;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Architecture.Modular;

/// <summary>
/// The dependency rules of the modules, applied to the real assemblies: inside a module, between modules, who may reference
/// the Endpoints and the Infrastructure of a module, and the frameworks each layer may use. The rules themselves are
/// proved on synthetic graphs in <see cref="ModuleReferenceRulesTests"/>, because a generated project has one module and
/// its projects reference very little yet. The rules that hold in every architecture are in
/// <see cref="BuildingBlockDependencyTests"/>.
/// </summary>
public class ModuleDependencyTests
{
    private static string Root => ReferenceRules.SolutionRoot;

    private static IReadOnlyList<string> Violations(
        IEnumerable<Assembly> assemblies, Func<string, IEnumerable<string>, string, IReadOnlyList<string>> rule) =>
        [.. assemblies.SelectMany(a => rule(ReferenceRules.NameOf(a), ReferenceRules.ReferencedNames(a), Root))];

    [Theory]
    [InlineData("Contracts")]
    [InlineData("Domain")]
    [InlineData("Application")]
    [InlineData("Infrastructure")]
    [InlineData("Endpoints")]
    public void EachProjectOfAModuleReferencesOnlyWhatItsLayerMayReference(string layer)
    {
        var projects = ModuleAssemblies.OfLayer(layer);

        Assert.NotEmpty(projects);
        ReferenceRules.AssertNone(Violations(projects.Select(p => p.Assembly), ModuleReferenceRules.LayerViolations));
    }

    [Fact]
    public void TheDomainAndContractsOfAModuleUseOnlyTheBaseClassLibrary()
    {
        var projects = ModuleAssemblies.Discover().Where(m => m.Name.Layer is "Domain" or "Contracts").ToList();

        Assert.NotEmpty(projects);
        ReferenceRules.AssertNone(Violations(projects.Select(p => p.Assembly), ModuleReferenceRules.FrameworkViolations));
    }

    [Fact]
    public void TheApplicationOfAModuleUsesNoWebOrDataFramework()
    {
        var projects = ModuleAssemblies.OfLayer("Application");

        Assert.NotEmpty(projects);
        ReferenceRules.AssertNone(Violations(projects.Select(p => p.Assembly), ModuleReferenceRules.FrameworkViolations));
    }

    // With one module this has nothing to look at: the synthetic graphs of ModuleReferenceRulesTests prove the rule.
    [Fact]
    public void AModuleReferencesOnlyTheContractsOfAnotherModule()
    {
        ReferenceRules.AssertNone(
            Violations(ModuleAssemblies.Discover().Select(m => m.Assembly), ModuleReferenceRules.CrossModuleViolations));
    }

    [Fact]
    public void OnlyTheHostReferencesTheEndpointsOfAModuleAndOnlyItsOwnEndpointsReferenceItsInfrastructure()
    {
        ReferenceRules.AssertNone(Violations(ModuleAssemblies.SolutionAssemblies(), ModuleReferenceRules.AccessViolations));
    }

    [Fact]
    public void NoBuildingBlockReferencesAModule()
    {
        ReferenceRules.AssertNone(Violations(ReferenceRules.BuildingBlocks, ModuleReferenceRules.BuildingBlockViolations));
    }

    [Fact]
    public void TheCodeOfTheHostUsesTheEndpointsProjectOfAModuleAndNoOtherProjectOfIt()
    {
        ReferenceRules.AssertNone(Violations([ReferenceRules.Api], ModuleReferenceRules.HostCodeViolations));
    }

    // Reads the project file, which lists what the host references directly: a reference to the Domain of a module would
    // compile (the Host would see it), so only the project file shows that it was added.
    [Fact]
    public void TheHostProjectFileReferencesNoProjectOfAModuleOtherThanItsEndpoints()
    {
        var references = HostProjectReferences();

        ReferenceRules.AssertNone(ModuleReferenceRules.HostProjectFileViolations(references, Root));
    }

    // Guards the rule above against being vacuous, and checks the other direction: the host lists the Endpoints project of
    // every module that exists, or the module would not be composed.
    [Fact]
    public void TheHostProjectFileReferencesTheEndpointsProjectOfEveryModule()
    {
        var references = HostProjectReferences();

        var missing = ModuleAssemblies.OfLayer("Endpoints")
            .Select(m => ReferenceRules.NameOf(m.Assembly))
            .Where(n => !references.Contains(n))
            .ToList();

        Assert.NotEmpty(ModuleAssemblies.OfLayer("Endpoints"));
        Assert.True(missing.Count == 0, $"The host's project file does not reference: {string.Join(", ", missing)}.");
    }

    // Guards the rule that exempts Endpoints from the web building block: the checker sees that they do reference it, as
    // they must (IModule and IEndpoint live there).
    [Fact]
    public void TheEndpointsOfAModuleActuallyReferenceTheWebBuildingBlocks()
    {
        var endpoints = ModuleAssemblies.OfLayer("Endpoints");

        Assert.NotEmpty(endpoints);
        Assert.All(
            endpoints,
            m => Assert.Contains(ReferenceRules.NameOf(ReferenceRules.BuildingBlocksWeb), ReferenceRules.ReferencedNames(m.Assembly)));
    }

    private static IReadOnlyList<string> HostProjectReferences()
    {
        var host = ReferenceRules.NameOf(ReferenceRules.Api);
        var file = Path.Combine(SolutionLocator.SolutionRoot(), "src", host, host + ".csproj");
        Assert.True(File.Exists(file), $"The project file of the host was not found at '{file}'.");
        return ModuleReferenceRules.ProjectReferencesOf(XDocument.Load(file));
    }
}
