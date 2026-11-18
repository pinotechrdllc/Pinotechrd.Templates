using System.Text.RegularExpressions;
using System.Xml.Linq;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Layout;

/// <summary>
/// Layout facts the generation harness cannot see from inside a build: the solution file lists the projects that exist,
/// and the README names only projects that exist. They run in the template source and in every generated project.
/// </summary>
public partial class SolutionLayoutTests
{
    private static readonly string[] ProjectFolders = ["src", "tests"];

    [Fact]
    public void SolutionListsExactlyTheProjectsOnDiskUnderSrcAndTests()
    {
        var root = SolutionLocator.SolutionRoot();

        var listed = ListedProjects(SolutionFile(root));
        var onDisk = ProjectsOnDisk(root);

        Assert.NotEmpty(listed);
        Assert.NotEmpty(onDisk);
        var problems = Differences(listed, onDisk, path => File.Exists(Path.Combine(root, path)));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void ReadmeLayoutTableNamesOnlyProjectsAndFoldersThatExist()
    {
        var root = SolutionLocator.SolutionRoot();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));

        var named = ReadmeLayoutPaths(readme);

        Assert.NotEmpty(named);
        var missing = named
            .Where(p => !Directory.Exists(Path.Combine(root, p)))
            .Select(p => $"The README layout table names '{p}', which does not exist.")
            .ToList();
        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void ReadmeLayoutTableNamesTheHostAndTheTestProject()
    {
        // Non-vacuity: the extraction finds the entries that are always there, so an empty or broken parse cannot pass.
        var readme = File.ReadAllText(Path.Combine(SolutionLocator.SolutionRoot(), "README.md"));

        var named = ReadmeLayoutPaths(readme);

        Assert.Contains(named, p => p.StartsWith("src/", StringComparison.Ordinal) && p.EndsWith(".Api", StringComparison.Ordinal));
        Assert.Contains(named, p => p.StartsWith("tests/", StringComparison.Ordinal) && p.EndsWith(".Api.Tests", StringComparison.Ordinal));
    }

    // Self-tests of the comparison on synthetic cases: they prove that each side of the rule can fail.

    [Fact]
    public void ComparisonReportsAProjectListedInTheSolutionButMissingOnDisk()
    {
        var problems = Differences(
            listed: ["src/A/A.csproj", "src/B/B.csproj"],
            onDisk: ["src/A/A.csproj"],
            exists: path => path == "src/A/A.csproj");

        var problem = Assert.Single(problems);
        Assert.Contains("src/B/B.csproj", problem);
    }

    [Fact]
    public void ComparisonReportsAProjectOnDiskThatTheSolutionDoesNotList()
    {
        var problems = Differences(
            listed: ["src/A/A.csproj"],
            onDisk: ["src/A/A.csproj", "tests/A.Tests/A.Tests.csproj"],
            exists: _ => true);

        var problem = Assert.Single(problems);
        Assert.Contains("tests/A.Tests/A.Tests.csproj", problem);
    }

    [Fact]
    public void ComparisonAcceptsMatchingSetsIgnoringSeparatorStyle()
    {
        var problems = Differences(
            listed: ["src\\A\\A.csproj"],
            onDisk: ["src/A/A.csproj"],
            exists: _ => true);

        Assert.Empty(problems);
    }

    [Fact]
    public void SolutionParserReadsProjectsThatSitBetweenXmlComments()
    {
        var solution = XDocument.Parse(
            """
            <Solution>
              <Folder Name="/src/">
            <!-- a comment line -->
            <!-- another comment line -->
                <Project Path="src/Clean/Clean.csproj" />
            <!-- end of comments -->
                <Project Path="src/Api/Api.csproj" />
              </Folder>
            </Solution>
            """);

        Assert.Equal(["src/Clean/Clean.csproj", "src/Api/Api.csproj"], ListedProjects(solution));
    }

    [Fact]
    public void ReadmeParserTakesTheFirstCodeSpanOfEachTableRowAndNothingElse()
    {
        var readme =
            """
            Text mentioning `src/Elsewhere` outside a table.

            | Project | Responsibility |
            | --- | --- |
            | `src/App.Api` | Uses `src/Other` in the text. |
            | `src/BuildingBlocks/` | Folder. |
            | `tests/App.Api.Tests` | Tests. |
            | not code | `src/Ignored` |
            """;

        Assert.Equal(["src/App.Api", "src/BuildingBlocks", "tests/App.Api.Tests"], ReadmeLayoutPaths(readme));
    }

    private static XDocument SolutionFile(string root)
    {
        var files = Directory.EnumerateFiles(root, "*.slnx").ToList();
        Assert.Single(files);
        return XDocument.Load(files[0]);
    }

    private static IReadOnlyList<string> ListedProjects(XDocument solution) =>
        [.. solution.Descendants("Project").Select(p => Normalize((string)p.Attribute("Path")!))];

    private static IReadOnlyList<string> ProjectsOnDisk(string root) =>
        [.. ProjectFolders
            .Select(f => Path.Combine(root, f))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.csproj", SearchOption.AllDirectories))
            .Select(f => Normalize(Path.GetRelativePath(root, f)))
            .Where(p => !p.Split('/').Any(segment => segment is "bin" or "obj"))];

    private static IReadOnlyList<string> Differences(
        IEnumerable<string> listed, IEnumerable<string> onDisk, Func<string, bool> exists)
    {
        var listedSet = listed.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        var diskSet = onDisk.Select(Normalize).ToHashSet(StringComparer.Ordinal);

        return
        [
            .. listedSet.Where(p => !exists(p)).Order(StringComparer.Ordinal)
                .Select(p => $"The solution lists '{p}', which does not exist."),
            .. diskSet.Where(p => !listedSet.Contains(p)).Order(StringComparer.Ordinal)
                .Select(p => $"'{p}' exists under src/ or tests/ but the solution does not list it."),
        ];
    }

    /// <summary>The <c>src/</c> and <c>tests/</c> paths in the first code span of each row of a markdown table.</summary>
    private static IReadOnlyList<string> ReadmeLayoutPaths(string markdown) =>
        [.. markdown.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith('|'))
            .Select(line => FirstCell().Match(line))
            .Where(m => m.Success && (m.Groups[1].Value.StartsWith("src/", StringComparison.Ordinal)
                || m.Groups[1].Value.StartsWith("tests/", StringComparison.Ordinal)))
            .Select(m => m.Groups[1].Value.TrimEnd('/'))];

    private static string Normalize(string path) => path.Replace('\\', '/');

    [GeneratedRegex(@"^\|\s*`([^`]+)`")]
    private static partial Regex FirstCell();
}
