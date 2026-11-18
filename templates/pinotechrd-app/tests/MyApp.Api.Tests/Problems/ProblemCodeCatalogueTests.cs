using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Problems;

// Keeps contract/problem-codes.v1.md and the *ErrorCodes classes of the solution in sync.
public partial class ProblemCodeCatalogueTests
{
    private const string CatalogueRelativePath = "contract/problem-codes.v1.md";

    [GeneratedRegex(@"^\|\s*`(?<code>[^`]+)`\s*\|(?<status>[^|]*)\|(?<meaning>[^|]*)\|", RegexOptions.Multiline)]
    private static partial Regex CatalogueRow();

    [GeneratedRegex("^[a-z][a-z0-9]*(_[a-z0-9]+)*$")]
    private static partial Regex CodeFormat();

    // Breaks when a constant is added to or removed from a *ErrorCodes class without the catalogue (or the reverse).
    [Fact]
    public void CatalogueListsExactlyTheCodesDeclaredInTheErrorCodesClasses()
    {
        var declared = DeclaredCodes().Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        var listed = ReadCatalogue().Select(r => r.Code).ToHashSet(StringComparer.Ordinal);

        var missingFromCatalogue = declared.Except(listed).Order(StringComparer.Ordinal).ToArray();
        var missingFromCode = listed.Except(declared).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            missingFromCatalogue.Length == 0 && missingFromCode.Length == 0,
            $"Declared in code but missing from the catalogue: [{string.Join(", ", missingFromCatalogue)}]. "
            + $"Listed in the catalogue but not declared in any *ErrorCodes class: [{string.Join(", ", missingFromCode)}].");
    }

    // Guards the reflection itself: an empty scan would make the comparison above meaningless.
    [Fact]
    public void AtLeastOneErrorCodesClassIsFound()
    {
        Assert.NotEmpty(DeclaredCodes());
    }

    // Breaks when two classes declare the same value.
    [Fact]
    public void CodesAreUniqueAcrossAllErrorCodesClasses()
    {
        var duplicates = DeclaredCodes()
            .GroupBy(c => c.Value, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"'{g.Key}' in {string.Join(", ", g.Select(c => c.Source))}")
            .ToArray();

        Assert.True(duplicates.Length == 0, "Duplicate codes: " + string.Join("; ", duplicates));
    }

    // Breaks when a code is not lower snake_case.
    [Fact]
    public void CodesAreLowerSnakeCase()
    {
        var invalid = DeclaredCodes().Where(c => !CodeFormat().IsMatch(c.Value)).Select(c => $"'{c.Value}' in {c.Source}").ToArray();

        Assert.True(invalid.Length == 0, "Codes that are not lower snake_case: " + string.Join("; ", invalid));
    }

    [Fact]
    public void CatalogueCodesAreUniqueAndLowerSnakeCase()
    {
        var rows = ReadCatalogue();
        var duplicates = rows.GroupBy(r => r.Code, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        var invalid = rows.Where(r => !CodeFormat().IsMatch(r.Code)).Select(r => r.Code).ToArray();

        Assert.True(duplicates.Length == 0, "Duplicate catalogue rows: " + string.Join(", ", duplicates));
        Assert.True(invalid.Length == 0, "Invalid catalogue codes: " + string.Join(", ", invalid));
    }

    // Breaks when a catalogue row has an empty status or meaning.
    [Fact]
    public void EveryCatalogueCodeHasAStatusAndAMeaning()
    {
        var rows = ReadCatalogue();
        Assert.NotEmpty(rows);

        var incomplete = rows
            .Where(r => string.IsNullOrWhiteSpace(r.Status) || string.IsNullOrWhiteSpace(r.Meaning))
            .Select(r => r.Code)
            .ToArray();

        Assert.True(incomplete.Length == 0, "Catalogue codes without a status or meaning: " + string.Join(", ", incomplete));
    }

    internal static List<(string Code, string Status, string Meaning)> ReadCatalogue()
    {
        var path = Path.Combine(SolutionRoot(), CatalogueRelativePath);
        Assert.True(File.Exists(path), $"The catalogue was not found at '{path}'.");
        return [.. CatalogueRow().Matches(File.ReadAllText(path))
            .Select(m => (m.Groups["code"].Value.Trim(), m.Groups["status"].Value.Trim(), m.Groups["meaning"].Value.Trim()))];
    }

    private static string SolutionRoot() => SolutionLocator.SolutionRoot();

    private static List<(string Value, string Source)> DeclaredCodes() =>
        [.. SolutionAssemblies()
            .SelectMany(TypesOf)
            .Where(t => t is { IsPublic: true, IsAbstract: true, IsSealed: true } && t.Name.EndsWith("ErrorCodes", StringComparison.Ordinal))
            .DistinctBy(t => t.FullName)
            .SelectMany(t => t
                .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
                .Select(f => ((string)f.GetRawConstantValue()!, $"{t.Name}.{f.Name}")))];

    private static IEnumerable<Type> TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>().Where(t => t.IsPublic);
        }
    }

    // The projects of the solution share the root name of the test assembly (MyApp.Api.Tests gives MyApp.*), and each
    // one is copied next to the tests because the Api project references them. Test assemblies are skipped.
    private static List<Assembly> SolutionAssemblies()
    {
        var testAssemblyName = typeof(ProblemCodeCatalogueTests).Assembly.GetName().Name!;
        var root = testAssemblyName[..testAssemblyName.LastIndexOf(".Api.Tests", StringComparison.Ordinal)];
        var assemblies = new List<Assembly>();
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, root + ".*.dll"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.EndsWith(".Tests", StringComparison.Ordinal))
            {
                continue;
            }

            assemblies.Add(
                Array.Find(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == name)
                ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(file));
        }

        return assemblies;
    }
}
