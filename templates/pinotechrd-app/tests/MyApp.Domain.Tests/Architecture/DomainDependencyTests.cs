namespace MyApp.Domain.Tests.Architecture;

public class DomainDependencyTests
{
    private static readonly string[] ForbiddenFrameworkPrefixes =
    [
        "Microsoft.AspNetCore",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Extensions",
        "Microsoft.Data",
        "Npgsql",
        "Dapper",
        "Wolverine",
        "System.Data",
        "System.Net.Http",
    ];

    private static readonly string[] BaseClassLibraryPrefixes = ["System", "netstandard", "mscorlib"];

    private static bool HasPrefix(string name, string prefix) =>
        name == prefix || name.StartsWith(prefix + ".", StringComparison.Ordinal);

    [Fact]
    public void DomainReferencesNoOtherSolutionAssembly()
    {
        var domain = DomainAssembly.Assembly.GetName().Name!;
        var solutionPrefix = domain[..domain.LastIndexOf('.')];

        var violations = DomainAssembly.Assembly.GetReferencedAssemblies()
            .Select(r => r.Name!)
            .Where(n => HasPrefix(n, solutionPrefix))
            .Select(n => $"Assembly '{domain}' must not reference solution assembly '{n}'.")
            .ToList();

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void DomainReferencesOnlyTheBaseClassLibraryAndNoWebDataOrMessagingFramework()
    {
        var domain = DomainAssembly.Assembly.GetName().Name!;
        var violations = new List<string>();
        foreach (var name in DomainAssembly.Assembly.GetReferencedAssemblies().Select(r => r.Name!))
        {
            if (ForbiddenFrameworkPrefixes.FirstOrDefault(p => HasPrefix(name, p)) is { } prefix)
            {
                violations.Add($"Assembly '{domain}' must not reference '{name}' (forbidden '{prefix}*').");
            }
            else if (!BaseClassLibraryPrefixes.Any(p => HasPrefix(name, p)))
            {
                violations.Add($"Assembly '{domain}' must not reference '{name}' (only the base class library is allowed).");
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }
}
