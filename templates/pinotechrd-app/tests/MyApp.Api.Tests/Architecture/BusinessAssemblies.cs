using System.Reflection;

namespace MyApp.Api.Tests.Architecture;

/// <summary>
/// Finds the business assemblies of the solution at run time: every <c>{Root}.*.dll</c> next to the tests that is not a
/// building block, not the host and not a test assembly. A layer or module added later is found with no change here.
/// </summary>
internal static class BusinessAssemblies
{
    /// <summary>The business assemblies next to the running tests, by assembly name.</summary>
    public static IReadOnlyList<string> Discover() => Discover(AppContext.BaseDirectory, ReferenceRules.SolutionRoot);

    public static IReadOnlyList<string> Discover(string directory, string root) =>
        Classify(
            Directory.EnumerateFiles(directory, root + ".*.dll").Select(f => Path.GetFileNameWithoutExtension(f)!),
            root);

    /// <summary>Picks the business assemblies out of the names of the assemblies of a solution.</summary>
    public static IReadOnlyList<string> Classify(IEnumerable<string> assemblyNames, string root) =>
        [.. assemblyNames
            .Where(n => ReferenceRules.HasPrefix(n, root)
                && !ReferenceRules.HasPrefix(n, root + ".BuildingBlocks")
                && n != root + ".Api"
                && !n.EndsWith(".Tests", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)];

    /// <summary>Loads the discovered assemblies so their references can be read.</summary>
    public static IReadOnlyList<Assembly> Load(IEnumerable<string> names) =>
        [.. names.Select(n => Assembly.Load(new AssemblyName(n)))];

    /// <summary>
    /// Fails when a discovery did not find an assembly that must exist: an empty or short result would otherwise make
    /// every rule over the business assemblies pass without checking anything.
    /// </summary>
    public static void AssertFound(IReadOnlyList<string> discovered, IEnumerable<string> expected)
    {
        var missing = expected.Where(e => !discovered.Contains(e, StringComparer.Ordinal)).ToList();
        Assert.True(
            missing.Count == 0,
            $"The discovery of business assemblies did not find: {string.Join(", ", missing)}. Found: [{string.Join(", ", discovered)}].");
    }
}
