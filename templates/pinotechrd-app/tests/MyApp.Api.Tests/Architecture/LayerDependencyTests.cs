using System.Reflection;

namespace MyApp.Api.Tests.Architecture;

public class LayerDependencyTests
{
    private static IEnumerable<Assembly> AllExcept(params Assembly[] allowed) =>
        ReferenceRules.AllLayers.Where(a => !allowed.Contains(a));

    [Fact]
    public void DomainReferencesNoSolutionAssemblyAndOnlyTheBaseClassLibrary()
    {
        var violations = ReferenceRules.Violations(
                ReferenceRules.Domain,
                AllExcept(ReferenceRules.Domain),
                ReferenceRules.FrameworkPrefixesForbiddenInDomain)
            .Concat(ReferenceRules.NonBaseClassLibraryViolations(ReferenceRules.Domain))
            .Distinct()
            .ToList();

        ReferenceRules.AssertNone(violations);
    }

    [Fact]
    public void ContractsReferencesNoOtherSolutionAssembly()
    {
        ReferenceRules.AssertNone(ReferenceRules.Violations(
            ReferenceRules.Contracts, AllExcept(ReferenceRules.Contracts), []));
    }

    [Fact]
    public void ResourcesReferencesNoOtherSolutionAssembly()
    {
        ReferenceRules.AssertNone(ReferenceRules.Violations(
            ReferenceRules.Resources, AllExcept(ReferenceRules.Resources), []));
    }

    [Fact]
    public void ApplicationReferencesOnlyDomainContractsAndResourcesAndNoWebOrDataFramework()
    {
        ReferenceRules.AssertNone(ReferenceRules.Violations(
            ReferenceRules.Application,
            AllExcept(ReferenceRules.Application, ReferenceRules.Domain, ReferenceRules.Contracts, ReferenceRules.Resources),
            ReferenceRules.FrameworkPrefixesForbiddenInApplication));
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        ReferenceRules.AssertNone(ReferenceRules.Violations(
            ReferenceRules.Infrastructure, [ReferenceRules.Api], []));
    }

    [Fact]
    public void EachMarkerTypeLivesInADistinctAssemblyOfItsOwnLayer()
    {
        var markers = new (string Layer, Type Marker)[]
        {
            ("Domain", typeof(MyApp.Domain.DomainAssembly)),
            ("Application", typeof(MyApp.Application.ApplicationAssembly)),
            ("Contracts", typeof(MyApp.Contracts.ContractsAssembly)),
            ("Resources", typeof(MyApp.Resources.ResourcesAssembly)),
            ("Infrastructure", typeof(MyApp.Infrastructure.InfrastructureAssembly)),
            ("Api", typeof(MyApp.Api.ApiAssembly)),
        };

        foreach (var (layer, marker) in markers)
        {
            var name = marker.Assembly.GetName().Name!;
            Assert.True(
                name.EndsWith("." + layer, StringComparison.Ordinal),
                $"Marker '{marker.Name}' lives in assembly '{name}', expected an assembly ending in '.{layer}'.");
        }

        Assert.Equal(6, markers.Select(m => m.Marker.Assembly).Distinct().Count());
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
}
