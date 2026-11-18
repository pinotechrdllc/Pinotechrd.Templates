using System.Reflection;

namespace MyApp.Api.Tests.Architecture;

/// <summary>Reflection helpers that report forbidden references of an assembly.</summary>
internal static class ReferenceRules
{
    public static readonly string[] FrameworkPrefixesForbiddenInDomain =
    [
        "Microsoft.AspNetCore",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Extensions",
        "Npgsql",
        "Wolverine",
    ];

    public static readonly string[] FrameworkPrefixesForbiddenInApplication =
    [
        "Microsoft.AspNetCore",
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
    ];

    public static readonly string[] BaseClassLibraryPrefixes = ["System", "netstandard", "mscorlib"];

    public static Assembly Domain => MyApp.Domain.DomainAssembly.Assembly;

    public static Assembly Application => MyApp.Application.ApplicationAssembly.Assembly;

    public static Assembly Contracts => MyApp.Contracts.ContractsAssembly.Assembly;

    public static Assembly Resources => MyApp.Resources.ResourcesAssembly.Assembly;

    public static Assembly Infrastructure => MyApp.Infrastructure.InfrastructureAssembly.Assembly;

    public static Assembly Api => MyApp.Api.ApiAssembly.Assembly;

    public static IReadOnlyList<Assembly> AllLayers => [Domain, Application, Contracts, Resources, Infrastructure, Api];

    public static string NameOf(Assembly assembly) => assembly.GetName().Name!;

    public static IReadOnlyList<string> ReferencedNames(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies().Select(r => r.Name!).OrderBy(n => n, StringComparer.Ordinal)];

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

    /// <summary>Returns a message for every reference of <paramref name="assembly"/> outside the base class library.</summary>
    public static IReadOnlyList<string> NonBaseClassLibraryViolations(Assembly assembly) =>
        [.. ReferencedNames(assembly)
            .Where(r => !BaseClassLibraryPrefixes.Any(p => HasPrefix(r, p)))
            .Select(r => $"Assembly '{NameOf(assembly)}' must not reference '{r}' (only the base class library is allowed).")];

    public static void AssertNone(IReadOnlyList<string> violations) =>
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
}
