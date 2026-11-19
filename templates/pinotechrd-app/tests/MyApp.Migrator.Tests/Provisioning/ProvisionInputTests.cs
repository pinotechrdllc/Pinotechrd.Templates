using MyApp.Api.Tests.Database;

namespace MyApp.Migrator.Tests.Provisioning;

// Settings of `provision` that are refused before anything is read or written: exit 2, no row, and a report that never
// echoes the rejected value. Rejected values hold the marker "bad7q" so the report can be searched for it.
[Collection(PostgresContainer.Name)]
public class ProvisionInputTests(PostgresFixture postgres)
{
    private const string Marker = "bad7q";

    public static TheoryData<string> InvalidSlugs() => new()
    {
        "Bad7qUpper",
        "-bad7q",
        "bad7q-",
        "_bad7q",
        "bad7q_x",
        "bad7q x",
        " bad7q",
        "bad7q\"x",
        "bad7q;x",
        "bad7q--x",
        "bad7qé",
        "bad7q\n",
        Marker + new string('a', 59), // 64 characters
        string.Empty,
    };

    public static TheoryData<string> InvalidNames() => new()
    {
        "   ",
        string.Empty,
        "bad7q\u0007x",
        "bad7q\u001bx",
        Marker + new string('n', 196), // 201 characters
    };

    public static TheoryData<string> InvalidSubscriptions() => new()
    {
        "not-a-guid-bad7q",
        string.Empty,
        Guid.Empty.ToString("D"),
    };

    // Breaks when a slug that is not lower-case letters, digits and single inner hyphens is accepted, a row is written, or
    // the report repeats the rejected slug.
    [Theory]
    [MemberData(nameof(InvalidSlugs))]
    public async Task AnInvalidSlugExitsTwoCreatesNoRowAndIsNotEchoed(string slug)
    {
        var (database, subscription) = await ProvisionSupport.ArrangeAsync(postgres);

        var run = await ProvisionSupport.RunAsync(database, slug, "Acme", subscription);

        Assert.Equal(2, run.ExitCode);
        await AssertNothingCreatedAsync(database);
        AssertNotEchoed(run.Report, slug);
    }

    // Breaks when a name that is blank, has control characters or is over 200 characters is accepted or echoed.
    [Theory]
    [MemberData(nameof(InvalidNames))]
    public async Task AnInvalidNameExitsTwoCreatesNoRowAndIsNotEchoed(string name)
    {
        var (database, subscription) = await ProvisionSupport.ArrangeAsync(postgres);

        var run = await ProvisionSupport.RunAsync(database, "acme", name, subscription);

        Assert.Equal(2, run.ExitCode);
        await AssertNothingCreatedAsync(database);
        AssertNotEchoed(run.Report, name);
    }

    // Breaks when a subscription id that is not a non-empty GUID is accepted or echoed.
    [Theory]
    [MemberData(nameof(InvalidSubscriptions))]
    public async Task AnInvalidSubscriptionIdExitsTwoCreatesNoRowAndIsNotEchoed(string subscription)
    {
        var (database, _) = await ProvisionSupport.ArrangeAsync(postgres);

        var run = await ProvisionSupport.RunAsync(database, "acme", "Acme", subscription);

        Assert.Equal(2, run.ExitCode);
        await AssertNothingCreatedAsync(database);
        AssertNotEchoed(run.Report, subscription);
    }

    // Breaks when a missing slug, name or subscription id starts a provision instead of exit 2.
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task AMissingSettingExitsTwoAndCreatesNoRow(bool slug, bool name, bool subscription)
    {
        var (database, id) = await ProvisionSupport.ArrangeAsync(postgres);

        var run = await ProvisionSupport.RunAsync(
            database, slug ? "acme" : null, name ? "Acme" : null, subscription ? id.ToString("D") : null);

        Assert.Equal(2, run.ExitCode);
        await AssertNothingCreatedAsync(database);
    }

    private static async Task AssertNothingCreatedAsync(string database)
    {
        Assert.Empty(await ProvisionSupport.RowsAsync(database));
        Assert.Equal(0L, await ProvisionSupport.TenantSchemaCountAsync(database));
    }

    private static void AssertNotEchoed(string report, string rejected)
    {
        if (rejected.Length > 0)
        {
            Assert.DoesNotContain(rejected, report, StringComparison.Ordinal);
        }

        if (rejected.Contains(Marker, StringComparison.Ordinal))
        {
            Assert.DoesNotContain(Marker, report, StringComparison.OrdinalIgnoreCase);
        }
    }
}
