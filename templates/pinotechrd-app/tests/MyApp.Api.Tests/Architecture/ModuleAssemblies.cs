using System.Reflection;

namespace MyApp.Api.Tests.Architecture;

/// <summary>
/// The module assemblies of the project, discovered at run time by name (<c>{Root}.Modules.{Module}.{Layer}</c>) so a
/// module added later is checked with no change here. It is shared by both architectures because the Access module is part
/// of every generated project. The stamp module, which only the modular architecture keeps, is named in
/// <c>Modular/StampAssemblies.cs</c>, the other half of this class.
/// </summary>
internal static partial class ModuleAssemblies
{
    public static string Root => ReferenceRules.SolutionRoot;

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

    /// <summary>The module assemblies of one module.</summary>
    public static IReadOnlyList<(ModuleAssemblyName Name, Assembly Assembly)> OfModule(string module) =>
        [.. Discover().Where(m => m.Name.Module == module)];

    /// <summary>Every assembly a module rule looks at: the building blocks, the host and the business assemblies.</summary>
    public static IReadOnlyList<Assembly> SolutionAssemblies() =>
        [.. ReferenceRules.BuildingBlocks, ReferenceRules.Api, .. BusinessAssemblies.Load(BusinessAssemblies.Discover())];
}
