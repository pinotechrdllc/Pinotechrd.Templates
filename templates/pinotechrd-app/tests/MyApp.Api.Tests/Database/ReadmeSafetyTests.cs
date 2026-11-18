using System.Text.RegularExpressions;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Database;

// Criterion 22: the README's local database command stays safe and in step with the fixture.
public class ReadmeSafetyTests
{
    private static string DockerRunLine()
    {
        var readme = File.ReadAllText(Path.Combine(SolutionLocator.SolutionRoot(), "README.md"));
        return Assert.Single(
            readme.Split('\n'),
            l => l.TrimStart().StartsWith("docker run", StringComparison.Ordinal) && l.Contains("postgres", StringComparison.Ordinal));
    }

    // Breaks when the documented command publishes the database on every network interface.
    [Fact]
    public void DockerRunLineBindsThePostgresPortToTheLoopbackOnly()
    {
        var line = DockerRunLine();

        Assert.Matches(@"-p 127\.0\.0\.1:5432:5432(\s|$)", line);
        Assert.DoesNotMatch(@"-p\s+(0\.0\.0\.0:)?\d+:\d+", Regex.Replace(line, @"-p 127\.0\.0\.1:\d+:\d+", string.Empty));
        Assert.DoesNotContain("0.0.0.0", line, StringComparison.Ordinal);
    }

    // Breaks when the documented image differs from the fixture's image.
    [Fact]
    public void DockerRunLineUsesTheFixtureImage()
    {
        Assert.EndsWith(" " + PostgresFixture.Image, DockerRunLine().TrimEnd(), StringComparison.Ordinal);
    }
}
