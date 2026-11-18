using System.Reflection;

namespace MyApp.Api.Tests.Architecture.Modular;

/// <summary>
/// The module assemblies of the project, discovered at run time by name (<c>{Root}.Modules.{Module}.{Layer}</c>) so a
/// module added later is checked with no change here. This folder is removed from a clean project, so the stamp's marker
/// types are named only here and in the rules of this folder.
/// </summary>
internal static class ModuleAssemblies
{
    public static string Root => ReferenceRules.SolutionRoot;

    /// <summary>The five assemblies of the stamp module, through its marker types.</summary>
    public static IReadOnlyList<Assembly> Stamp =>
    [
        MyApp.Modules.MyModule.Contracts.MyModuleContractsAssembly.Assembly,
        MyApp.Modules.MyModule.Domain.MyModuleDomainAssembly.Assembly,
        MyApp.Modules.MyModule.Application.MyModuleApplicationAssembly.Assembly,
        MyApp.Modules.MyModule.Infrastructure.MyModuleInfrastructureAssembly.Assembly,
        MyApp.Modules.MyModule.Endpoints.MyModuleEndpointsAssembly.Assembly,
    ];

    /// <summary>The names of every business assembly under <c>{Root}.Modules.</c> next to the tests, well formed or not.</summary>
    public static IReadOnlyList<string> DiscoveredNames() =>
        [.. BusinessAssemblies.Discover().Where(n => ModuleAssemblyNames.IsModuleAssembly(n, Root))];

    /// <summary>The module assemblies next to the tests, loaded, each with its module and layer.</summary>
    public static IReadOnlyList<(ModuleAssemblyName Name, Assembly Assembly)> Discover() =>
        [.. DiscoveredNames()
            .Select(n => (Name: ModuleAssemblyNames.Parse(n, Root), Assembly: Assembly.Load(new AssemblyName(n))))
            .Where(m => m.Name is not null)
            .Select(m => (m.Name!, m.Assembly))];

    /// <summary>The module assemblies of one layer.</summary>
    public static IReadOnlyList<(ModuleAssemblyName Name, Assembly Assembly)> OfLayer(string layer) =>
        [.. Discover().Where(m => m.Name.Layer == layer)];

    /// <summary>Every assembly a module rule looks at: the building blocks, the host and the business assemblies.</summary>
    public static IReadOnlyList<Assembly> SolutionAssemblies() =>
        [.. ReferenceRules.BuildingBlocks, ReferenceRules.Api, .. BusinessAssemblies.Load(BusinessAssemblies.Discover())];
}
