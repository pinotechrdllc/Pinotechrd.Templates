using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Architecture;

/// <summary>
/// Which architecture the running tests belong to. A generated project keeps the rules of its own architecture only
/// (<c>Architecture/Clean</c> or <c>Architecture/Modular</c>); the template source keeps both and its host composes the
/// clean layers and the stamp module at once, which is the superset. A rule that counts assemblies asks here, so it
/// stays exact in each case instead of loosening to "at least".
/// </summary>
internal static class ArchitectureLayout
{
    /// <summary>True when the tests run in the template source (the superset), where both sets of rules exist.</summary>
    public static bool IsSuperset() =>
        HasBothRuleFolders(Path.Combine(SolutionLocator.SolutionRoot(), "tests", ReferenceRules.SolutionRoot + ".Api.Tests"));

    /// <summary>True when the test project at <paramref name="testProjectDirectory"/> has the rules of both architectures.</summary>
    public static bool HasBothRuleFolders(string testProjectDirectory) =>
        Directory.Exists(Path.Combine(testProjectDirectory, "Architecture", "Clean"))
        && Directory.Exists(Path.Combine(testProjectDirectory, "Architecture", "Modular"));
}

/// <summary>A business assembly of a module, named <c>{Root}.Modules.{Module}.{Layer}</c>.</summary>
/// <param name="Module">The module name.</param>
/// <param name="Layer">The project of the module: one of <see cref="ModuleAssemblyNames.Layers"/>.</param>
internal sealed record ModuleAssemblyName(string Module, string Layer);

/// <summary>Reads the names of module assemblies. Names only: it works in a project that has no module.</summary>
internal static class ModuleAssemblyNames
{
    /// <summary>The five projects of a module.</summary>
    public static readonly string[] Layers = ["Contracts", "Domain", "Application", "Infrastructure", "Endpoints"];

    /// <summary>True when the name is below <c>{root}.Modules.</c>, whether or not it is well formed.</summary>
    public static bool IsModuleAssembly(string name, string root) =>
        name.StartsWith(root + ".Modules.", StringComparison.Ordinal);

    /// <summary>The module and layer of a well formed module assembly name, or null for any other name.</summary>
    public static ModuleAssemblyName? Parse(string name, string root)
    {
        if (!IsModuleAssembly(name, root))
        {
            return null;
        }

        var parts = name[(root.Length + ".Modules.".Length)..].Split('.');
        return parts.Length == 2 && parts[0].Length > 0 && Array.IndexOf(Layers, parts[1]) >= 0
            ? new ModuleAssemblyName(parts[0], parts[1])
            : null;
    }

    /// <summary>True for the Endpoints project of a module.</summary>
    public static bool IsEndpoints(string name, string root) => Parse(name, root) is { Layer: "Endpoints" };

    /// <summary>
    /// The assemblies that may reference the web building block: the host and the Endpoints project of a module, which
    /// holds the module's composition root and its endpoints. Any other assembly must not.
    /// </summary>
    public static bool MayReferenceTheWebBuildingBlocks(string name, string root) =>
        name == root + ".Api" || IsEndpoints(name, root);

    /// <summary>The module assemblies (<c>{root}.Modules.*.Endpoints</c>) among the assembly files of a folder.</summary>
    public static IReadOnlyList<string> EndpointsAssembliesIn(string directory, string root) =>
        [.. Directory.EnumerateFiles(directory, root + ".Modules.*.Endpoints.dll")
            .Select(f => Path.GetFileNameWithoutExtension(f)!)
            .Where(n => IsEndpoints(n, root))
            .Order(StringComparer.Ordinal)];
}
