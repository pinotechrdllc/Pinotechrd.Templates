using System.Reflection;

namespace MyApp.Api.Tests.Architecture;

/// <summary>
/// Reflection helpers that report forbidden references of an assembly. Shared by both architectures: it names the
/// building blocks and the host only, never an assembly that exists in one architecture alone.
/// </summary>
internal static class ReferenceRules
{
    public static readonly string[] AdoAndHttpPrefixes = ["System.Data", "System.Net.Http"];

    public static readonly string[] FrameworkPrefixesForbiddenInDomain =
    [
        "Microsoft.AspNetCore",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Extensions",
        "Microsoft.Data",
        "Npgsql",
        "Dapper",
        "Wolverine",
        .. AdoAndHttpPrefixes,
    ];

    public static readonly string[] FrameworkPrefixesForbiddenInApplication =
    [
        "Microsoft.AspNetCore",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Data",
        "Npgsql",
        "Dapper",
        "Wolverine",
        .. AdoAndHttpPrefixes,
    ];

    public static readonly string[] BaseClassLibraryPrefixes = ["System", "netstandard", "mscorlib"];

    public static Assembly BuildingBlocksDomain => MyApp.BuildingBlocks.Domain.BuildingBlocksDomainAssembly.Assembly;

    public static Assembly BuildingBlocksApplication => MyApp.BuildingBlocks.Application.BuildingBlocksApplicationAssembly.Assembly;

    public static Assembly BuildingBlocksInfrastructure => MyApp.BuildingBlocks.Infrastructure.BuildingBlocksInfrastructureAssembly.Assembly;

    public static Assembly BuildingBlocksWeb => MyApp.BuildingBlocks.Web.BuildingBlocksWebAssembly.Assembly;

    public static Assembly Api => MyApp.Api.ApiAssembly.Assembly;

    public static IReadOnlyList<Assembly> BuildingBlocks =>
        [BuildingBlocksDomain, BuildingBlocksApplication, BuildingBlocksInfrastructure, BuildingBlocksWeb];

    /// <summary>
    /// The name every assembly of the solution starts with, taken from the name of the test assembly
    /// (<c>{Root}.Api.Tests</c>) so it survives the renaming of the template placeholder.
    /// </summary>
    public static string SolutionRoot { get; } = RootOf(typeof(ReferenceRules).Assembly.GetName().Name!);

    public static string RootOf(string testAssemblyName)
    {
        const string Suffix = ".Api.Tests";
        Assert.EndsWith(Suffix, testAssemblyName, StringComparison.Ordinal);
        return testAssemblyName[..^Suffix.Length];
    }

    public static string NameOf(Assembly assembly) => assembly.GetName().Name!;

    public static IReadOnlyList<string> ReferencedNames(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies().Select(r => r.Name!).OrderBy(n => n, StringComparer.Ordinal)];

    public static bool IsBaseClassLibrary(string name) =>
        BaseClassLibraryPrefixes.Any(p => HasPrefix(name, p)) && !AdoAndHttpPrefixes.Any(p => HasPrefix(name, p));

    public static bool HasPrefix(string name, string prefix) =>
        name == prefix || name.StartsWith(prefix + ".", StringComparison.Ordinal);

    /// <summary>True when the name is an assembly of this solution (it starts with the solution root).</summary>
    public static bool IsSolutionAssembly(string name) => HasPrefix(name, SolutionRoot);

    /// <summary>Returns one message per reference of <paramref name="assembly"/> that is a forbidden solution assembly or forbidden prefix.</summary>
    public static IReadOnlyList<string> Violations(
        Assembly assembly,
        IEnumerable<Assembly> forbiddenSolutionAssemblies,
        IEnumerable<string> forbiddenPrefixes) =>
        ViolationsByName(assembly, forbiddenSolutionAssemblies.Select(NameOf), forbiddenPrefixes);

    /// <summary>The same check with the forbidden solution assemblies given by name.</summary>
    public static IReadOnlyList<string> ViolationsByName(
        Assembly assembly,
        IEnumerable<string> forbiddenSolutionAssemblyNames,
        IEnumerable<string> forbiddenPrefixes)
    {
        var forbiddenSolution = forbiddenSolutionAssemblyNames.ToHashSet(StringComparer.Ordinal);
        var prefixes = forbiddenPrefixes.ToArray();
        var messages = new List<string>();
        foreach (var reference in ReferencedNames(assembly))
        {
            if (forbiddenSolution.Contains(reference))
            {
                messages.Add($"Assembly '{NameOf(assembly)}' must not reference solution assembly '{reference}'.");
            }
            else if (prefixes.FirstOrDefault(p => HasPrefix(reference, p)) is { } prefix)
            {
                messages.Add($"Assembly '{NameOf(assembly)}' must not reference '{reference}' (forbidden '{prefix}*').");
            }
        }

        return messages;
    }

    /// <summary>
    /// Returns a message for every reference of <paramref name="assembly"/> to a solution assembly that is not in
    /// <paramref name="allowed"/>, whatever that assembly is called: it does not depend on a list of the assemblies.
    /// </summary>
    public static IReadOnlyList<string> SolutionReferenceViolations(Assembly assembly, params Assembly[] allowed)
    {
        var allowedNames = allowed.Select(NameOf).ToHashSet(StringComparer.Ordinal);
        return [.. ReferencedNames(assembly)
            .Where(r => IsSolutionAssembly(r) && !allowedNames.Contains(r))
            .Select(r => $"Assembly '{NameOf(assembly)}' must not reference solution assembly '{r}'.")];
    }

    /// <summary>
    /// Returns a message for every reference of <paramref name="assembly"/> outside the base class library,
    /// except the solution assemblies in <paramref name="allowedSolutionAssemblies"/>.
    /// </summary>
    public static IReadOnlyList<string> NonBaseClassLibraryViolations(
        Assembly assembly,
        params Assembly[] allowedSolutionAssemblies)
    {
        var allowed = allowedSolutionAssemblies.Select(NameOf).ToHashSet(StringComparer.Ordinal);
        return [.. ReferencedNames(assembly)
            .Where(r => !IsBaseClassLibrary(r) && !allowed.Contains(r))
            .Select(r => $"Assembly '{NameOf(assembly)}' must not reference '{r}' (only the base class library is allowed).")];
    }

    public static void AssertNone(IReadOnlyList<string> violations) =>
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
}
