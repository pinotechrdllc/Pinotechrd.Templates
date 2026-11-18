using System.Reflection;

namespace MyApp.Api.Tests.Architecture;

public class LayerDependencyTests
{
    private static IEnumerable<Assembly> AllExcept(params Assembly[] allowed) =>
        ReferenceRules.AllSolutionAssemblies.Where(a => !allowed.Contains(a));

    [Fact]
    public void DomainReferencesOnlyTheDomainBuildingBlocksAmongSolutionAssembliesAndOnlyTheBaseClassLibrary()
    {
        var violations = ReferenceRules.Violations(
                ReferenceRules.Domain,
                AllExcept(ReferenceRules.Domain, ReferenceRules.BuildingBlocksDomain),
                ReferenceRules.FrameworkPrefixesForbiddenInDomain)
            .Concat(ReferenceRules.NonBaseClassLibraryViolations(ReferenceRules.Domain, ReferenceRules.BuildingBlocksDomain))
            .Distinct()
            .ToList();

        ReferenceRules.AssertNone(violations);
    }

    [Fact]
    public void DomainBuildingBlocksReferenceNoOtherSolutionAssemblyAndOnlyTheBaseClassLibrary()
    {
        var violations = ReferenceRules.Violations(
                ReferenceRules.BuildingBlocksDomain,
                AllExcept(ReferenceRules.BuildingBlocksDomain),
                ReferenceRules.FrameworkPrefixesForbiddenInDomain)
            .Concat(ReferenceRules.NonBaseClassLibraryViolations(ReferenceRules.BuildingBlocksDomain))
            .Distinct()
            .ToList();

        ReferenceRules.AssertNone(violations);
    }

    [Fact]
    public void ApplicationBuildingBlocksReferenceOnlyTheDomainBuildingBlocksAmongSolutionAssembliesAndNoWebOrDataFramework()
    {
        ReferenceRules.AssertNone(ReferenceRules.Violations(
            ReferenceRules.BuildingBlocksApplication,
            AllExcept(ReferenceRules.BuildingBlocksApplication, ReferenceRules.BuildingBlocksDomain),
            ReferenceRules.FrameworkPrefixesForbiddenInApplication));
    }

    [Fact]
    public void WebBuildingBlocksReferenceOnlyTheApplicationAndDomainBuildingBlocksAmongSolutionAssemblies()
    {
        ReferenceRules.AssertNone(ReferenceRules.Violations(
            ReferenceRules.BuildingBlocksWeb,
            AllExcept(
                ReferenceRules.BuildingBlocksWeb,
                ReferenceRules.BuildingBlocksApplication,
                ReferenceRules.BuildingBlocksDomain),
            []));
    }

    [Fact]
    public void WebBuildingBlocksActuallyUseAspNetCore()
    {
        // Guards the rules above against being vacuous: the checker sees ASP.NET Core references of this assembly.
        Assert.Contains(
            ReferenceRules.ReferencedNames(ReferenceRules.BuildingBlocksWeb),
            n => ReferenceRules.HasPrefix(n, "Microsoft.AspNetCore"));
    }

    [Fact]
    public void OnlyTheApiReferencesTheWebBuildingBlocks()
    {
        var violations = AllExcept(ReferenceRules.Api, ReferenceRules.BuildingBlocksWeb)
            .SelectMany(a => ReferenceRules.Violations(a, [ReferenceRules.BuildingBlocksWeb], []))
            .ToList();

        ReferenceRules.AssertNone(violations);
    }

    [Fact]
    public void NoBuildingBlockReferencesABusinessAssembly()
    {
        var violations = ReferenceRules.BuildingBlocks
            .SelectMany(bb => ReferenceRules.Violations(bb, ReferenceRules.AllLayers, []))
            .ToList();

        ReferenceRules.AssertNone(violations);
    }

    [Fact]
    public void ContractsReferencesNoOtherSolutionAssemblyAndNoFramework()
    {
        ReferenceRules.AssertNone(ReferenceRules.Violations(
            ReferenceRules.Contracts, AllExcept(ReferenceRules.Contracts), ReferenceRules.FrameworkPrefixesForbiddenInDomain));
    }

    [Fact]
    public void ResourcesReferencesNoOtherSolutionAssemblyAndNoFramework()
    {
        ReferenceRules.AssertNone(ReferenceRules.Violations(
            ReferenceRules.Resources, AllExcept(ReferenceRules.Resources), ReferenceRules.FrameworkPrefixesForbiddenInDomain));
    }

    [Fact]
    public void ApplicationReferencesOnlyDomainContractsResourcesAndBuildingBlocksAndNoWebOrDataFramework()
    {
        ReferenceRules.AssertNone(ReferenceRules.Violations(
            ReferenceRules.Application,
            AllExcept(
                ReferenceRules.Application,
                ReferenceRules.Domain,
                ReferenceRules.Contracts,
                ReferenceRules.Resources,
                ReferenceRules.BuildingBlocksApplication,
                ReferenceRules.BuildingBlocksDomain),
            ReferenceRules.FrameworkPrefixesForbiddenInApplication));
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        ReferenceRules.AssertNone(ReferenceRules.Violations(
            ReferenceRules.Infrastructure, [ReferenceRules.Api], []));
    }

    // Breaks when a layer other than the infrastructure (the API composes it and may name nothing of Npgsql here) or a
    // building block starts to reference Npgsql: the web building block had no rule for it before.
    [Fact]
    public void OnlyTheInfrastructureAndTheApiMayReferenceNpgsql()
    {
        var violations = AllExcept(ReferenceRules.Infrastructure, ReferenceRules.Api)
            .SelectMany(a => ReferenceRules.Violations(a, [], ["Npgsql"]))
            .ToList();

        ReferenceRules.AssertNone(violations);
    }

    // Guards the rule above against being vacuous: the checker sees Npgsql where it is allowed.
    [Fact]
    public void InfrastructureActuallyReferencesNpgsql()
    {
        Assert.Contains(ReferenceRules.ReferencedNames(ReferenceRules.Infrastructure), n => ReferenceRules.HasPrefix(n, "Npgsql"));
    }

    // Breaks when the infrastructure learns about the web host (the health tag belongs to the Api composition).
    [Fact]
    public void InfrastructureReferencesNeitherTheApiNorTheWebBuildingBlocks()
    {
        ReferenceRules.AssertNone(ReferenceRules.Violations(
            ReferenceRules.Infrastructure, [ReferenceRules.Api, ReferenceRules.BuildingBlocksWeb], []));
    }

    // Breaks when the web building block learns about a business assembly (the infrastructure) or the other way round.
    [Fact]
    public void WebBuildingBlocksDoNotReferenceTheInfrastructure()
    {
        ReferenceRules.AssertNone(ReferenceRules.Violations(
            ReferenceRules.BuildingBlocksWeb, [ReferenceRules.Infrastructure], []));
    }

    [Fact]
    public void EachMarkerTypeLivesInADistinctAssemblyOfItsOwnLayer()
    {
        var markers = new (string Layer, Type Marker)[]
        {
            ("BuildingBlocks.Domain", typeof(MyApp.BuildingBlocks.Domain.BuildingBlocksDomainAssembly)),
            ("BuildingBlocks.Application", typeof(MyApp.BuildingBlocks.Application.BuildingBlocksApplicationAssembly)),
            ("Domain", typeof(MyApp.Domain.DomainAssembly)),
            ("Application", typeof(MyApp.Application.ApplicationAssembly)),
            ("Contracts", typeof(MyApp.Contracts.ContractsAssembly)),
            ("Resources", typeof(MyApp.Resources.ResourcesAssembly)),
            ("Infrastructure", typeof(MyApp.Infrastructure.InfrastructureAssembly)),
            ("BuildingBlocks.Web", typeof(MyApp.BuildingBlocks.Web.BuildingBlocksWebAssembly)),
            ("Api", typeof(MyApp.Api.ApiAssembly)),
        };

        foreach (var (layer, marker) in markers)
        {
            var name = marker.Assembly.GetName().Name!;
            Assert.True(
                name.EndsWith("." + layer, StringComparison.Ordinal),
                $"Marker '{marker.Name}' lives in assembly '{name}', expected an assembly ending in '.{layer}'.");
        }

        Assert.Equal(9, markers.Select(m => m.Marker.Assembly).Distinct().Count());
    }

    [Fact]
    public void RuleCheckerReportsAssemblyAndForbiddenReferenceWhenViolated()
    {
        // The Api layer really uses ASP.NET Core, so asking "Api must not reference Microsoft.AspNetCore"
        // must fail with a message naming both sides. This proves the checker can detect a violation.
        var violations = ReferenceRules.Violations(ReferenceRules.Api, [], ["Microsoft.AspNetCore"]);

        Assert.NotEmpty(violations);
        Assert.All(violations, m =>
        {
            Assert.Contains(ReferenceRules.NameOf(ReferenceRules.Api), m);
            Assert.Contains("Microsoft.AspNetCore", m);
        });
    }

    [Fact]
    public void NonBaseClassLibraryCheckReportsASolutionReferenceUnlessItIsAllowed()
    {
        // The application building blocks really use the DI abstractions, so they must be reported
        // unless allowed, and an allowed solution assembly must be exempt.
        var reported = ReferenceRules.NonBaseClassLibraryViolations(ReferenceRules.BuildingBlocksApplication);

        Assert.Contains(reported, m => m.Contains("Microsoft.Extensions.DependencyInjection.Abstractions", StringComparison.Ordinal));

        var exempt = ReferenceRules.NonBaseClassLibraryViolations(
            ReferenceRules.Application, ReferenceRules.BuildingBlocksApplication, ReferenceRules.BuildingBlocksDomain);
        Assert.DoesNotContain(exempt, m => m.Contains(ReferenceRules.NameOf(ReferenceRules.BuildingBlocksApplication), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("System.Data.Common")]
    [InlineData("System.Data")]
    [InlineData("System.Net.Http")]
    public void AdoNetAndHttpAreNotTreatedAsBaseClassLibrary(string name)
    {
        Assert.False(ReferenceRules.IsBaseClassLibrary(name));
    }

    [Theory]
    [InlineData("System.Runtime")]
    [InlineData("System.Collections")]
    [InlineData("netstandard")]
    public void OrdinarySystemAssembliesAreBaseClassLibrary(string name)
    {
        Assert.True(ReferenceRules.IsBaseClassLibrary(name));
    }
}
