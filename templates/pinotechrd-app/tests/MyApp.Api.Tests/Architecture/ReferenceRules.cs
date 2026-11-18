using System.Reflection;

namespace MyApp.Api.Tests.Architecture;

/// <summary>Reflection helpers that report forbidden references of an assembly.</summary>
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

    public static Assembly Domain => MyApp.Domain.DomainAssembly.Assembly;

    public static Assembly Application => MyApp.Application.ApplicationAssembly.Assembly;

    public static Assembly Contracts => MyApp.Contracts.ContractsAssembly.Assembly;

    public static Assembly Resources => MyApp.Resources.ResourcesAssembly.Assembly;

    public static Assembly Infrastructure => MyApp.Infrastructure.InfrastructureAssembly.Assembly;

    public static Assembly Api => MyApp.Api.ApiAssembly.Assembly;

    /// <summary>The business assemblies: every assembly of the solution that is not a building block.</summary>
    public static IReadOnlyList<Assembly> AllLayers => [Domain, Application, Contracts, Resources, Infrastructure, Api];

    public static IReadOnlyList<Assembly> BuildingBlocks => [BuildingBlocksDomain, BuildingBlocksApplication];

    /// <summary>Every assembly of the solution that a rule may name: the business assemblies and the building blocks.</summary>
    public static IReadOnlyList<Assembly> AllSolutionAssemblies => [.. AllLayers, .. BuildingBlocks];

    public static string NameOf(Assembly assembly) => assembly.GetName().Name!;

    public static IReadOnlyList<string> ReferencedNames(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies().Select(r => r.Name!).OrderBy(n => n, StringComparer.Ordinal)];

    public static bool IsBaseClassLibrary(string name) =>
        BaseClassLibraryPrefixes.Any(p => HasPrefix(name, p)) && !AdoAndHttpPrefixes.Any(p => HasPrefix(name, p));

    public static bool HasPrefix(string name, string prefix) =>
        name == prefix || name.StartsWith(prefix + ".", StringComparison.Ordinal);

    /// <summary>Returns one message per reference of <paramref name="assembly"/> that is a forbidden solution assembly or forbidden prefix.</summary>
    public static IReadOnlyList<string> Violations(
        Assembly assembly,
        IEnumerable<Assembly> forbiddenSolutionAssemblies,
        IEnumerable<string> forbiddenPrefixes)
    {
        var forbiddenSolution = forbiddenSolutionAssemblies.Select(NameOf).ToHashSet(StringComparer.Ordinal);
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
