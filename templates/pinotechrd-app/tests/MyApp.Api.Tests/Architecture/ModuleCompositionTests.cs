using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Web;

namespace MyApp.Api.Tests.Architecture;

/// <summary>
/// How the real host composes the modules, checked in both architectures: a clean project has no module and the rules
/// hold with nothing to compare, a modular one has its modules, the template source has the stamp. The fixture modules
/// of the test assembly are not part of the real host, so they appear only in the self-tests of the rules.
/// </summary>
public class ModuleCompositionTests(ApiHostFactory host) : IClassFixture<ApiHostFactory>
{
    private static string Root => ReferenceRules.SolutionRoot;

    // Breaks when a module on disk is not composed by the host, is composed twice, or the host composes a module whose
    // assemblies are not there.
    [Fact]
    public void TheHostComposesExactlyTheModulesThatExistOnDisk()
    {
        var onDisk = ModuleAssemblyNames.EndpointsAssembliesIn(AppContext.BaseDirectory, Root);
        var registered = host.Services.GetServices<IModule>().Select(m => m.EndpointsAssembly.GetName().Name!);

        ReferenceRules.AssertNone(ModuleCompositionRules.CompositionViolations(onDisk, registered));
    }

    // Breaks when a module's registration stops calling AddValidation(): its request bodies would not be validated.
    [Fact]
    public void EveryModuleWithEndpointsContributedAValidationResolverOfItsAssemblyToTheHost()
    {
        var modules = host.Services.GetServices<IModule>().ToList();

        var violations = ModuleCompositionRules.MissingValidationResolvers(modules, ValidationResolvers.AssemblyNamesOf(host.Services));

        ReferenceRules.AssertNone(violations);
    }

    // Self-tests of the composition rule on synthetic names.

    [Fact]
    public void ModulesThatMatchTheAssembliesOnDiskAreAccepted()
    {
        Assert.Empty(ModuleCompositionRules.CompositionViolations(["Acme.Modules.A.Endpoints", "Acme.Modules.B.Endpoints"], ["Acme.Modules.B.Endpoints", "Acme.Modules.A.Endpoints"]));
        Assert.Empty(ModuleCompositionRules.CompositionViolations([], []));
    }

    [Fact]
    public void AModuleOnDiskThatTheHostDoesNotComposeIsReported()
    {
        var violation = Assert.Single(ModuleCompositionRules.CompositionViolations(["Acme.Modules.A.Endpoints", "Acme.Modules.B.Endpoints"], ["Acme.Modules.A.Endpoints"]));

        Assert.Contains("Acme.Modules.B.Endpoints", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void AModuleRegisteredTwiceIsReported()
    {
        var violation = Assert.Single(ModuleCompositionRules.CompositionViolations(["Acme.Modules.A.Endpoints"], ["Acme.Modules.A.Endpoints", "Acme.Modules.A.Endpoints"]));

        Assert.Contains("Acme.Modules.A.Endpoints", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void ARegisteredModuleWithoutAssembliesOnDiskIsReported()
    {
        var violation = Assert.Single(ModuleCompositionRules.CompositionViolations([], ["Acme.Modules.Ghost.Endpoints"]));

        Assert.Contains("Acme.Modules.Ghost.Endpoints", violation, StringComparison.Ordinal);
    }

    // Self-tests of the validation rule on the fixture modules of the test assembly: the one that calls AddValidation(),
    // the one that does not, and one that has nothing to validate.

    [Fact]
    public void AModuleWithEndpointsThatCalledAddValidationIsAccepted()
    {
        var violations = Violations<FixtureValidatedModule>();

        Assert.Empty(violations);
    }

    [Fact]
    public void AModuleWithEndpointsThatDidNotCallAddValidationIsReportedByName()
    {
        var violation = Assert.Single(Violations<FixtureUnvalidatedModule>());

        Assert.Contains("Unvalidated", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void AModuleWithoutEndpointsIsNotRequiredToContributeAResolver()
    {
        Assert.Empty(Violations<FixtureQuietModule>());
    }

    private static IReadOnlyList<string> Violations<TModule>()
        where TModule : class, IModule, new()
    {
        var builder = ModuleBuilders.Create();
        builder.Services.AddOptions();
        builder.AddModule<TModule>();
        using var provider = builder.Services.BuildServiceProvider();

        return ModuleCompositionRules.MissingValidationResolvers(
            provider.GetServices<IModule>(), ValidationResolvers.AssemblyNamesOf(provider));
    }
}

/// <summary>The composition rules of the modules, as functions over names and modules.</summary>
internal static class ModuleCompositionRules
{
    /// <summary>
    /// The endpoints assemblies of the modules on disk against those of the modules the host registered: each must be
    /// registered exactly once, and a registered module must have its assemblies on disk.
    /// </summary>
    public static IReadOnlyList<string> CompositionViolations(IEnumerable<string> onDisk, IEnumerable<string> registered)
    {
        var disk = onDisk.ToList();
        var counts = registered.GroupBy(n => n, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return
        [
            .. disk.Where(n => counts.GetValueOrDefault(n) != 1)
                .Select(n => $"The module assembly '{n}' is on disk and the host registers {counts.GetValueOrDefault(n)} modules for it, expected one."),
            .. counts.Keys.Where(n => !disk.Contains(n, StringComparer.Ordinal))
                .Select(n => $"The host registers a module whose endpoints assembly '{n}' is not next to the tests."),
        ];
    }

    /// <summary>
    /// One message per module whose endpoints assembly contains an <see cref="IEndpoint"/> implementation but did not
    /// contribute a validation resolver: its project never called <c>AddValidation()</c>. A module that exposes nothing
    /// has nothing to validate and is not required to.
    /// </summary>
    public static IReadOnlyList<string> MissingValidationResolvers(IEnumerable<IModule> modules, IEnumerable<string> resolverAssemblyNames)
    {
        var contributed = resolverAssemblyNames.ToHashSet(StringComparer.Ordinal);
        return
        [
            .. modules
                .Where(m => HasEndpoints(m.EndpointsAssembly) && !contributed.Contains(m.EndpointsAssembly.GetName().Name!))
                .Select(m => $"The module '{m.Name}' has endpoints in '{m.EndpointsAssembly.GetName().Name}' but no validation resolver of that assembly was registered: its AddServices must call AddValidation()."),
        ];
    }

    private static bool HasEndpoints(Assembly assembly) =>
        assembly.GetTypes().Any(t => t is { IsClass: true, IsAbstract: false } && typeof(IEndpoint).IsAssignableFrom(t));
}
