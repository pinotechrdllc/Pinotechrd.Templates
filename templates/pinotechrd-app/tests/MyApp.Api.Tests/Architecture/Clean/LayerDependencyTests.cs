using System.Reflection;

namespace MyApp.Api.Tests.Architecture.Clean;

/// <summary>
/// Dependency rules of the five layers of a clean project. The rules that hold in both architectures (building blocks,
/// host, Npgsql, web building block) live in <see cref="BuildingBlockDependencyTests"/>.
/// </summary>
public class LayerDependencyTests
{
    private static IEnumerable<Assembly> AllExcept(params Assembly[] allowed) =>
        CleanLayers.AllSolutionAssemblies.Where(a => !allowed.Contains(a));

    [Fact]
    public void DomainReferencesOnlyTheDomainBuildingBlocksAmongSolutionAssembliesAndOnlyTheBaseClassLibrary()
    {
        var violations = ReferenceRules.Violations(
                CleanLayers.Domain,
                AllExcept(CleanLayers.Domain, ReferenceRules.BuildingBlocksDomain),
                ReferenceRules.FrameworkPrefixesForbiddenInDomain)
            .Concat(ReferenceRules.NonBaseClassLibraryViolations(CleanLayers.Domain, ReferenceRules.BuildingBlocksDomain))
            .Distinct()
            .ToList();

        ReferenceRules.AssertNone(violations);
    }

    [Fact]
    public void ContractsReferencesNoOtherSolutionAssemblyAndNoFramework()
    {
        ReferenceRules.AssertNone(ReferenceRules.Violations(
            CleanLayers.Contracts, AllExcept(CleanLayers.Contracts), ReferenceRules.FrameworkPrefixesForbiddenInDomain));
    }

    [Fact]
    public void ResourcesReferencesNoOtherSolutionAssemblyAndNoFramework()
    {
        ReferenceRules.AssertNone(ReferenceRules.Violations(
            CleanLayers.Resources, AllExcept(CleanLayers.Resources), ReferenceRules.FrameworkPrefixesForbiddenInDomain));
    }

    [Fact]
    public void ApplicationReferencesOnlyDomainContractsResourcesAndBuildingBlocksAndNoWebOrDataFramework()
    {
        ReferenceRules.AssertNone(ReferenceRules.Violations(
            CleanLayers.Application,
            AllExcept(
                CleanLayers.Application,
                CleanLayers.Domain,
                CleanLayers.Contracts,
                CleanLayers.Resources,
                ReferenceRules.BuildingBlocksApplication,
                ReferenceRules.BuildingBlocksDomain),
            ReferenceRules.FrameworkPrefixesForbiddenInApplication));
    }

    // Breaks when the infrastructure layer learns about the host or the web building block, or when it references a
    // solution assembly outside the layers it may use: the application and its dependencies, and the building blocks
    // for the domain, the handlers and the database.
    [Fact]
    public void InfrastructureReferencesOnlyTheInnerLayersAndTheNonWebBuildingBlocksAmongSolutionAssemblies()
    {
        ReferenceRules.AssertNone(ReferenceRules.Violations(
            CleanLayers.Infrastructure,
            AllExcept(
                CleanLayers.Infrastructure,
                CleanLayers.Application,
                CleanLayers.Domain,
                CleanLayers.Contracts,
                CleanLayers.Resources,
                ReferenceRules.BuildingBlocksDomain,
                ReferenceRules.BuildingBlocksApplication,
                ReferenceRules.BuildingBlocksInfrastructure),
            []));
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        ReferenceRules.AssertNone(ReferenceRules.Violations(
            CleanLayers.Infrastructure, [ReferenceRules.Api], []));
    }

    // Breaks when the infrastructure learns about the web host (the health tag belongs to the Api composition).
    [Fact]
    public void InfrastructureReferencesNeitherTheApiNorTheWebBuildingBlocks()
    {
        ReferenceRules.AssertNone(ReferenceRules.Violations(
            CleanLayers.Infrastructure, [ReferenceRules.Api, ReferenceRules.BuildingBlocksWeb], []));
    }

    // Breaks when the web building block learns about the infrastructure layer.
    [Fact]
    public void WebBuildingBlocksDoNotReferenceTheInfrastructureLayer()
    {
        ReferenceRules.AssertNone(ReferenceRules.Violations(
            ReferenceRules.BuildingBlocksWeb, [CleanLayers.Infrastructure], []));
    }

    [Fact]
    public void NoBuildingBlockReferencesALayer()
    {
        var violations = ReferenceRules.BuildingBlocks
            .SelectMany(bb => ReferenceRules.Violations(bb, CleanLayers.All, []))
            .ToList();

        ReferenceRules.AssertNone(violations);
    }

    // Since the database code moved to the infrastructure building block, only that assembly and the host hold Npgsql:
    // the infrastructure layer must reach the database through the building block.
    [Fact]
    public void OnlyTheInfrastructureAssembliesAndTheApiMayReferenceNpgsql()
    {
        var violations = AllExcept(CleanLayers.Infrastructure, ReferenceRules.BuildingBlocksInfrastructure, ReferenceRules.Api)
            .SelectMany(a => ReferenceRules.Violations(a, [], ["Npgsql"]))
            .ToList();

        ReferenceRules.AssertNone(violations);
    }

    // Replaces the old check that the infrastructure layer references Npgsql, which is no longer true: the database code
    // lives in the infrastructure building block. What is true now, and what the layer relies on, is that its project
    // references that building block; without it the layer would have no shared data source to build on.
    [Fact]
    public void InfrastructureProjectReferencesTheInfrastructureBuildingBlock()
    {
        var root = ReferenceRules.SolutionRoot;
        var project = Path.Combine(Support.SolutionLocator.SolutionRoot(), "src", $"{root}.Infrastructure", $"{root}.Infrastructure.csproj");

        var references = System.Xml.Linq.XDocument.Load(project)
            .Descendants("ProjectReference")
            .Select(e => ProjectName((string?)e.Attribute("Include") ?? string.Empty))
            .ToList();

        Assert.Contains(ReferenceRules.NameOf(ReferenceRules.BuildingBlocksInfrastructure), references);
    }

    // MSBuild accepts both separators in an Include path on every platform, but System.IO on Linux treats a backslash as
    // part of a file name, so the project name is cut from the string itself.
    private static string ProjectName(string include)
    {
        var file = include[(include.LastIndexOfAny(['\\', '/']) + 1)..];
        return file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? file[..^".csproj".Length] : file;
    }

    [Fact]
    public void ProjectNameIsReadFromPathsWithEitherSeparator()
    {
        Assert.Equal("A.B", ProjectName(@"..\BuildingBlocks\A.B\A.B.csproj"));
        Assert.Equal("A.B", ProjectName("../X/A.B.csproj"));
        Assert.Equal("A.B", ProjectName(@"..\X\A.B.csproj"));
    }

    [Fact]
    public void EachMarkerTypeLivesInADistinctAssemblyOfItsOwnProject()
    {
        var markers = new (string Project, Type Marker)[]
        {
            ("BuildingBlocks.Domain", typeof(MyApp.BuildingBlocks.Domain.BuildingBlocksDomainAssembly)),
            ("BuildingBlocks.Application", typeof(MyApp.BuildingBlocks.Application.BuildingBlocksApplicationAssembly)),
            ("BuildingBlocks.Infrastructure", typeof(MyApp.BuildingBlocks.Infrastructure.BuildingBlocksInfrastructureAssembly)),
            ("BuildingBlocks.Web", typeof(MyApp.BuildingBlocks.Web.BuildingBlocksWebAssembly)),
            ("Domain", typeof(MyApp.Domain.DomainAssembly)),
            ("Application", typeof(MyApp.Application.ApplicationAssembly)),
            ("Contracts", typeof(MyApp.Contracts.ContractsAssembly)),
            ("Resources", typeof(MyApp.Resources.ResourcesAssembly)),
            ("Infrastructure", typeof(MyApp.Infrastructure.InfrastructureAssembly)),
            ("Api", typeof(MyApp.Api.ApiAssembly)),
        };

        foreach (var (project, marker) in markers)
        {
            var name = marker.Assembly.GetName().Name!;
            Assert.True(
                name.EndsWith("." + project, StringComparison.Ordinal),
                $"Marker '{marker.Name}' lives in assembly '{name}', expected an assembly ending in '.{project}'.");
        }

        Assert.Equal(10, markers.Select(m => m.Marker.Assembly).Distinct().Count());
    }
}
