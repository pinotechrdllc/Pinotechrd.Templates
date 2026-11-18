namespace MyApp.Api.Tests.Database;

// Without Docker the database tests fail with a message that names Docker; they are never skipped.
public class PostgresFixtureTests
{
    // Breaks when a fixture that cannot reach Docker starts silently, hangs, or reports a failure that does not name Docker.
    [Fact]
    public async Task FixtureWithoutDockerFailsWithAMessageThatNamesDocker()
    {
        var fixture = PostgresFixture.ForDockerEndpoint("tcp://127.0.0.1:1");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.InitializeAsync());

        Assert.Contains("Docker", exception.Message, StringComparison.Ordinal);
        await fixture.DisposeAsync();
    }

    // Breaks when the pinned image drifts from the version the README tells a developer to run, or stops being a pinned minor version.
    [Fact]
    public void ImageIsTheSameMinorVersionAsTheReadmeDockerRunLine()
    {
        var readme = File.ReadAllText(Path.Combine(Support.SolutionLocator.SolutionRoot(), "README.md"));

        Assert.Contains(" " + PostgresFixture.Image + "\n", readme.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Matches(@"^postgres:\d+\.\d+$", PostgresFixture.Image);
    }
}
