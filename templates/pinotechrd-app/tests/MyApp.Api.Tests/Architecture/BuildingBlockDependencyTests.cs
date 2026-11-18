namespace MyApp.Api.Tests.Architecture;

/// <summary>
/// Dependency rules that hold in every architecture: the building blocks, the host and the discovered business
/// assemblies. Nothing here names an assembly that exists in one architecture only.
/// </summary>
public class BuildingBlockDependencyTests
{
    private static IReadOnlyList<string> BusinessNames => BusinessAssemblies.Discover();

    /// <summary>Every assembly of the solution that is checked: the building blocks, the host and the business assemblies.</summary>
    private static IReadOnlyList<System.Reflection.Assembly> SolutionAssemblies =>
        [.. ReferenceRules.BuildingBlocks, ReferenceRules.Api, .. BusinessAssemblies.Load(BusinessNames)];

    [Fact]
    public void EachMarkerTypeLivesInADistinctAssemblyOfItsOwnProject()
    {
        var markers = new (string Project, Type Marker)[]
        {
            ("BuildingBlocks.Domain", typeof(MyApp.BuildingBlocks.Domain.BuildingBlocksDomainAssembly)),
            ("BuildingBlocks.Application", typeof(MyApp.BuildingBlocks.Application.BuildingBlocksApplicationAssembly)),
            ("BuildingBlocks.Infrastructure", typeof(MyApp.BuildingBlocks.Infrastructure.BuildingBlocksInfrastructureAssembly)),
            ("BuildingBlocks.Web", typeof(MyApp.BuildingBlocks.Web.BuildingBlocksWebAssembly)),
            ("Api", typeof(MyApp.Api.ApiAssembly)),
        };

        foreach (var (project, marker) in markers)
        {
            var name = marker.Assembly.GetName().Name!;
            Assert.True(
                name.EndsWith("." + project, StringComparison.Ordinal),
                $"Marker '{marker.Name}' lives in assembly '{name}', expected an assembly ending in '.{project}'.");
        }

        Assert.Equal(markers.Length, markers.Select(m => m.Marker.Assembly).Distinct().Count());
    }

    [Fact]
    public void DomainBuildingBlocksReferenceOnlyTheBaseClassLibrary()
    {
        var violations = ReferenceRules.Violations(
                ReferenceRules.BuildingBlocksDomain, [], ReferenceRules.FrameworkPrefixesForbiddenInDomain)
            .Concat(ReferenceRules.SolutionReferenceViolations(ReferenceRules.BuildingBlocksDomain))
            .Concat(ReferenceRules.NonBaseClassLibraryViolations(ReferenceRules.BuildingBlocksDomain))
            .Distinct()
            .ToList();

        ReferenceRules.AssertNone(violations);
    }

    [Fact]
    public void ApplicationBuildingBlocksReferenceOnlyTheDomainBuildingBlocksAmongSolutionAssembliesAndNoWebOrDataFramework()
    {
        var violations = ReferenceRules.SolutionReferenceViolations(
                ReferenceRules.BuildingBlocksApplication, ReferenceRules.BuildingBlocksDomain)
            .Concat(ReferenceRules.Violations(
                ReferenceRules.BuildingBlocksApplication, [], ReferenceRules.FrameworkPrefixesForbiddenInApplication))
            .ToList();

        ReferenceRules.AssertNone(violations);
    }

    // The health-check and hosting abstractions are Microsoft.Extensions.* packages and carry no Microsoft.AspNetCore
    // reference, so the rule is exact: no ASP.NET Core at all, no web building block, no other solution assembly.
    [Fact]
    public void InfrastructureBuildingBlocksReferenceNoWebFrameworkAndNotTheWebBuildingBlocks()
    {
        var violations = ReferenceRules.Violations(
                ReferenceRules.BuildingBlocksInfrastructure,
                [ReferenceRules.BuildingBlocksWeb],
                ["Microsoft.AspNetCore"])
            .Concat(ReferenceRules.SolutionReferenceViolations(
                ReferenceRules.BuildingBlocksInfrastructure,
                ReferenceRules.BuildingBlocksDomain,
                ReferenceRules.BuildingBlocksApplication))
            .Distinct()
            .ToList();

        ReferenceRules.AssertNone(violations);
    }

    // Guards the rule above against being vacuous: the checker sees the abstractions this assembly really uses.
    [Fact]
    public void InfrastructureBuildingBlocksActuallyUseTheHealthCheckAndHostingAbstractions()
    {
        var names = ReferenceRules.ReferencedNames(ReferenceRules.BuildingBlocksInfrastructure);

        Assert.Contains(names, n => n == "Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions");
        Assert.Contains(names, n => n == "Microsoft.Extensions.Hosting.Abstractions");
    }

    [Fact]
    public void WebBuildingBlocksReferenceOnlyTheApplicationAndDomainBuildingBlocksAmongSolutionAssembliesAndNotNpgsql()
    {
        var violations = ReferenceRules.SolutionReferenceViolations(
                ReferenceRules.BuildingBlocksWeb,
                ReferenceRules.BuildingBlocksApplication,
                ReferenceRules.BuildingBlocksDomain)
            .Concat(ReferenceRules.Violations(
                ReferenceRules.BuildingBlocksWeb, [ReferenceRules.BuildingBlocksInfrastructure], ["Npgsql"]))
            .Distinct()
            .ToList();

        ReferenceRules.AssertNone(violations);
    }

    [Fact]
    public void WebBuildingBlocksActuallyUseAspNetCore()
    {
        // Guards the rules against being vacuous: the checker sees ASP.NET Core references of an assembly that uses it.
        Assert.Contains(
            ReferenceRules.ReferencedNames(ReferenceRules.BuildingBlocksWeb),
            n => ReferenceRules.HasPrefix(n, "Microsoft.AspNetCore"));
    }

    // Holds whatever the business assemblies are called: it looks at every reference to the solution, so it needs no
    // discovery and still fails in a project that has no business assembly yet.
    [Fact]
    public void BuildingBlocksReferenceOnlyOtherBuildingBlocksAmongSolutionAssemblies()
    {
        var buildingBlockNames = ReferenceRules.BuildingBlocks.Select(ReferenceRules.NameOf).ToHashSet(StringComparer.Ordinal);

        var violations = ReferenceRules.BuildingBlocks
            .SelectMany(bb => ReferenceRules.ReferencedNames(bb)
                .Where(r => ReferenceRules.IsSolutionAssembly(r) && !buildingBlockNames.Contains(r))
                .Select(r => $"Building block '{ReferenceRules.NameOf(bb)}' must not reference solution assembly '{r}'."))
            .ToList();

        ReferenceRules.AssertNone(violations);
    }

    [Fact]
    public void NoBuildingBlockReferencesABusinessAssembly()
    {
        var business = BusinessNames;

        var violations = ReferenceRules.BuildingBlocks
            .SelectMany(bb => ReferenceRules.ViolationsByName(bb, business, []))
            .ToList();

        ReferenceRules.AssertNone(violations);
    }

    [Fact]
    public void BusinessAssemblyDiscoveryNeverReturnsABuildingBlockTheHostOrATestAssembly()
    {
        string[] notBusiness = [.. ReferenceRules.BuildingBlocks.Select(ReferenceRules.NameOf), ReferenceRules.NameOf(ReferenceRules.Api)];

        var found = BusinessNames;

        Assert.DoesNotContain(found, n => notBusiness.Contains(n));
        Assert.DoesNotContain(found, n => n.EndsWith(".Tests", StringComparison.Ordinal));
    }

    // Only the infrastructure assemblies (any assembly whose name ends in .Infrastructure: the building block, the
    // infrastructure layer of a clean project, the infrastructure of a module) and the host may reference Npgsql.
    [Fact]
    public void OnlyInfrastructureAssembliesAndTheHostReferenceNpgsql()
    {
        var violations = SolutionAssemblies
            .Where(a => ReferenceRules.NameOf(a) != ReferenceRules.NameOf(ReferenceRules.Api)
                && !ReferenceRules.NameOf(a).EndsWith(".Infrastructure", StringComparison.Ordinal))
            .SelectMany(a => ReferenceRules.Violations(a, [], ["Npgsql"]))
            .ToList();

        ReferenceRules.AssertNone(violations);
    }

    // Guards the rule above against being vacuous: the checker sees Npgsql where it is allowed.
    [Fact]
    public void InfrastructureBuildingBlocksActuallyReferenceNpgsql()
    {
        Assert.Contains(
            ReferenceRules.ReferencedNames(ReferenceRules.BuildingBlocksInfrastructure),
            n => ReferenceRules.HasPrefix(n, "Npgsql"));
    }

    // The host and the Endpoints project of a module (its composition root and its endpoints) are the web layer; every
    // other assembly, a module's Domain, Application or Infrastructure included, must not reference it. The exemption is
    // by name, so the rule is exact in a clean project (no module), in a modular one and in the template source.
    [Fact]
    public void OnlyTheHostAndTheEndpointsOfModulesReferenceTheWebBuildingBlocks()
    {
        var violations = SolutionAssemblies
            .Where(a => a != ReferenceRules.BuildingBlocksWeb
                && !ModuleAssemblyNames.MayReferenceTheWebBuildingBlocks(ReferenceRules.NameOf(a), ReferenceRules.SolutionRoot))
            .SelectMany(a => ReferenceRules.Violations(a, [ReferenceRules.BuildingBlocksWeb], []))
            .ToList();

        ReferenceRules.AssertNone(violations);
    }

    // Self-test of the exemption above: only the host and a module's Endpoints project are exempt.
    [Theory]
    [InlineData("Acme.Api", true)]
    [InlineData("Acme.Modules.Sales.Endpoints", true)]
    [InlineData("Acme.Modules.Sales.Application", false)]
    [InlineData("Acme.Modules.Sales.Infrastructure", false)]
    [InlineData("Acme.Modules.Sales.Domain", false)]
    [InlineData("Acme.Modules.Sales.Contracts", false)]
    [InlineData("Acme.Modules.Endpoints", false)]
    [InlineData("Acme.Modules.Sales.Endpoints.Extra", false)]
    [InlineData("Acme.Sales.Endpoints", false)]
    [InlineData("Acme.Application", false)]
    [InlineData("Acme.Infrastructure", false)]
    [InlineData("Acme.BuildingBlocks.Infrastructure", false)]
    [InlineData("Other.Modules.Sales.Endpoints", false)]
    public void OnlyTheHostAndModuleEndpointsMayReferenceTheWebBuildingBlocks(string assembly, bool allowed)
    {
        Assert.Equal(allowed, ModuleAssemblyNames.MayReferenceTheWebBuildingBlocks(assembly, "Acme"));
    }

    // Guards the rule above: the host does reference the web building block, so the checker can see such a reference.
    [Fact]
    public void TheHostActuallyReferencesTheWebAndInfrastructureBuildingBlocks()
    {
        var names = ReferenceRules.ReferencedNames(ReferenceRules.Api);

        Assert.Contains(ReferenceRules.NameOf(ReferenceRules.BuildingBlocksWeb), names);
        Assert.Contains(ReferenceRules.NameOf(ReferenceRules.BuildingBlocksInfrastructure), names);
    }

    // Self-tests of the checker and of the discovery, on synthetic cases: in a project with no business assembly the
    // rules above have nothing to look at, so these prove that they can fail.

    [Fact]
    public void RuleCheckerReportsAssemblyAndForbiddenReferenceWhenViolated()
    {
        // The host really uses ASP.NET Core, so asking "Api must not reference Microsoft.AspNetCore"
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
    public void BusinessAssemblyRuleReportsABuildingBlockThatReferencesAnAssemblyNamedAsBusiness()
    {
        // Pretend the domain building block is a business assembly: the web building block references it,
        // so the rule over business assemblies must report that.
        var pretendBusiness = new[] { ReferenceRules.NameOf(ReferenceRules.BuildingBlocksDomain) };

        var violations = ReferenceRules.ViolationsByName(ReferenceRules.BuildingBlocksWeb, pretendBusiness, []);

        Assert.Single(violations);
        Assert.Contains(pretendBusiness[0], violations[0]);
    }

    [Fact]
    public void SolutionReferenceCheckReportsAReferenceThatIsNotAllowed()
    {
        // The web building block really uses the domain building block.
        var reported = ReferenceRules.SolutionReferenceViolations(ReferenceRules.BuildingBlocksWeb);

        Assert.Contains(reported, m => m.Contains(ReferenceRules.NameOf(ReferenceRules.BuildingBlocksDomain), StringComparison.Ordinal));
        Assert.Empty(ReferenceRules.SolutionReferenceViolations(ReferenceRules.BuildingBlocksWeb, ReferenceRules.BuildingBlocksDomain));
    }

    [Fact]
    public void ClassifyKeepsOnlyBusinessAssembliesOfTheSolution()
    {
        var names = new[]
        {
            "Acme.Api", "Acme.Api.Tests", "Acme.BuildingBlocks.Web", "Acme.BuildingBlocks.Tests", "Acme.Domain.Tests",
            "Acme.Sales.Domain", "Acme.Sales.Infrastructure", "Acme.Sales.Domain", "Acme.Apix", "Acmex.Domain", "Other.Domain", "System.Runtime",
        };

        var business = BusinessAssemblies.Classify(names, "Acme");

        Assert.Equal(["Acme.Apix", "Acme.Sales.Domain", "Acme.Sales.Infrastructure"], business);
    }

    [Fact]
    public void DiscoveryReadsTheAssembliesNextToTheTestsAndIgnoresOtherFiles()
    {
        var directory = Directory.CreateTempSubdirectory("discovery-").FullName;
        try
        {
            foreach (var file in new[]
            {
                "Acme.Api.dll", "Acme.Api.Tests.dll", "Acme.BuildingBlocks.Domain.dll", "Acme.Sales.Domain.dll",
                "Acme.Sales.Domain.pdb", "Acme.Sales.Domain.Tests.dll", "Other.Thing.dll",
            })
            {
                File.WriteAllBytes(Path.Combine(directory, file), []);
            }

            Assert.Equal(["Acme.Sales.Domain"], BusinessAssemblies.Discover(directory, "Acme"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ADiscoveryThatFindsNothingWhereSomethingIsExpectedFails()
    {
        var directory = Directory.CreateTempSubdirectory("discovery-empty-").FullName;
        try
        {
            var empty = BusinessAssemblies.Discover(directory, "Acme");

            Assert.Empty(empty);
            var failure = Assert.ThrowsAny<Exception>(() => BusinessAssemblies.AssertFound(empty, ["Acme.Domain"]));
            Assert.Contains("Acme.Domain", failure.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ADiscoveryThatFindsEverythingExpectedPasses()
    {
        BusinessAssemblies.AssertFound(["Acme.Domain", "Acme.Application"], ["Acme.Domain"]);
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

    [Fact]
    public void NonBaseClassLibraryCheckReportsASolutionReferenceUnlessItIsAllowed()
    {
        // The web building block really uses ASP.NET Core and the domain building block, so both must be reported
        // unless allowed, and an allowed solution assembly must be exempt.
        var reported = ReferenceRules.NonBaseClassLibraryViolations(ReferenceRules.BuildingBlocksWeb);

        Assert.Contains(reported, m => m.Contains("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.Contains(reported, m => m.Contains(ReferenceRules.NameOf(ReferenceRules.BuildingBlocksDomain), StringComparison.Ordinal));

        var exempt = ReferenceRules.NonBaseClassLibraryViolations(
            ReferenceRules.BuildingBlocksWeb, ReferenceRules.BuildingBlocksDomain);
        Assert.DoesNotContain(exempt, m => m.Contains(ReferenceRules.NameOf(ReferenceRules.BuildingBlocksDomain), StringComparison.Ordinal));
    }

    [Fact]
    public void SolutionRootIsDerivedFromTheTestAssemblyName()
    {
        Assert.Equal("Acme.Demo", ReferenceRules.RootOf("Acme.Demo.Api.Tests"));
        Assert.StartsWith(ReferenceRules.SolutionRoot + ".", ReferenceRules.NameOf(ReferenceRules.Api), StringComparison.Ordinal);
    }
}
