using System.Text.RegularExpressions;
using MyApp.Api.Tests.Database;
using MyApp.Migrator.Tests.Support;
using MyApp.Modules.Access.Application.Tenants;

namespace MyApp.Migrator.Tests.Provisioning;

// The command line shape of `provision`, hostile names and the slug pattern. A refused run exits 2 before the catalog is
// touched: on a database that was never migrated nothing is created, not even the catalog schema.
[Collection(PostgresContainer.Name)]
public class ProvisionCommandLineTests(PostgresFixture postgres)
{
    private const string Marker = "bad7q";

    private static string[] Settings(Guid subscription, string slug = "acme", string name = "Acme") =>
    [
        $"--Provision:Slug={slug}",
        $"--Provision:Name={name}",
        $"--Provision:SubscriptionId={subscription:D}",
    ];

    public static TheoryData<string> MisplacedVerbShapes() => new()
    {
        "verb-not-first",
        "dashdash-verb",
        "verb-with-value",
        "extra-positional",
        "settings-without-verb",
        "uppercase-verb-not-first",
        "uppercase-verb-first",
    };

    private static string[] Shape(string shape, string database, Guid subscription)
    {
        var connection = $"--{MigratorRun.ConnectionKey}={database}";
        var settings = Settings(subscription);
        return shape switch
        {
            "verb-not-first" => [connection, "provision", .. settings],
            "dashdash-verb" => ["--provision", connection, .. settings],
            "verb-with-value" => ["provision=acme", connection, .. settings],
            "extra-positional" => ["provision", "now", connection, .. settings],
            "settings-without-verb" => [connection, .. settings],
            "uppercase-verb-not-first" => [connection, "PROVISION", .. settings],
            "uppercase-verb-first" => ["PROVISION", connection, .. settings],
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }

    // Breaks when a verb that is not exactly the first argument, an extra positional, settings without the verb or another
    // casing of the verb start a migration or a provision instead of exit 2.
    [Theory]
    [MemberData(nameof(MisplacedVerbShapes))]
    public async Task AMisplacedOrMalformedVerbExitsTwoAndAppliesNothing(string shape)
    {
        var database = await postgres.CreateDatabaseAsync();
        var subscription = Guid.NewGuid();

        var run = await MigratorRun.ExecuteRawAsync(Shape(shape, database, subscription));

        Assert.Equal(2, run.ExitCode);
        await AssertUntouchedAsync(database);
        Assert.DoesNotContain("acme", run.Report, StringComparison.OrdinalIgnoreCase);
    }

    // Breaks when Provision settings that arrive by environment variable, with no verb, start a migration or a provision.
    [Fact]
    public async Task ProvisionSettingsFromTheEnvironmentWithoutTheVerbExitTwoAndApplyNothing()
    {
        var database = await postgres.CreateDatabaseAsync();
        const string SlugKey = "Provision__Slug";
        var previous = Environment.GetEnvironmentVariable(SlugKey);
        Environment.SetEnvironmentVariable(SlugKey, "acme");
        try
        {
            var run = await MigratorRun.ExecuteRawAsync($"--{MigratorRun.ConnectionKey}={database}");

            Assert.Equal(2, run.ExitCode);
            await AssertUntouchedAsync(database);
            Assert.DoesNotContain("acme", run.Report, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SlugKey, previous);
        }
    }

    private static string Name(params int[] codeUnits) => string.Concat(codeUnits.Select(c => c < 0 ? "bad7q" : ((char)c).ToString()));

    // The hostile name is built in the test body from its code units: theory data would be serialized and a lone
    // surrogate would reach the command as U+FFFD. -1 stands for the marker "bad7q".
    public static TheoryData<string> HostileNameKinds() => new()
    {
        "rtl-override",
        "zero-width-space",
        "byte-order-mark",
        "line-separator",
        "paragraph-separator",
        "lone-surrogate",
        "only-invisible-three",
        "only-invisible-one",
    };

    private static string HostileName(string kind) => kind switch
    {
        "rtl-override" => Name(-1, 0x202E, 'e'),
        "zero-width-space" => Name(-1, 0x200B, 'x'),
        "byte-order-mark" => Name(-1, 0xFEFF, 'x'),
        "line-separator" => Name(-1, 0x2028, 'x'),
        "paragraph-separator" => Name(-1, 0x2029, 'x'),
        "lone-surrogate" => Name(-1, 0xD800, 'x'),
        "only-invisible-three" => Name(0x200B, 0x200B, 0x200B),
        "only-invisible-one" => Name(0x200B),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // Breaks when a name with bidi, invisible, separator or malformed-UTF-16 characters, or one that only looks blank,
    // is accepted, a row is written, or the report repeats it.
    [Theory]
    [MemberData(nameof(HostileNameKinds))]
    public async Task AHostileNameExitsTwoCreatesNoRowAndIsNotEchoed(string kind)
    {
        var name = HostileName(kind);
        var (database, subscription) = await ProvisionSupport.ArrangeAsync(postgres);

        var run = await ProvisionSupport.RunAsync(database, "acme", name, subscription);

        Assert.Equal(2, run.ExitCode);
        Assert.Empty(await ProvisionSupport.RowsAsync(database));
        Assert.Equal(0L, await ProvisionSupport.TenantSchemaCountAsync(database));
        Assert.DoesNotContain(name, run.Report, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, run.Report, StringComparison.OrdinalIgnoreCase);
    }

    // Breaks when the exported pattern lets a trailing newline through ($ instead of \z).
    [Fact]
    public void TheExportedSlugPatternRejectsATrailingNewline()
    {
        Assert.DoesNotMatch(ProvisionTenantRequest.SlugPattern, "acme\n");
    }

    public static TheoryData<string> Slugs() => new()
    {
        "a", "acme", "a1", "1a", "a-b", "a-b-c", "0", "acme-2", new string('a', 63),
        string.Empty, "-", "-a", "a-", "a--b", "A", "Acme", "a_b", "a b", " a", "a ", "a\n", "\na", "a\r\n", "é", "aé",
        "a​", new string('a', 64), "a.b", "a/b",
    };

    // Breaks when IsValidSlug and the public pattern disagree on any slug, in either direction.
    [Theory]
    [MemberData(nameof(Slugs))]
    public void IsValidSlugAgreesWithTheExportedPattern(string slug)
    {
        Assert.Equal(Regex.IsMatch(slug, ProvisionTenantRequest.SlugPattern), ProvisionTenantRequest.IsValidSlug(slug));
    }

    // Breaks when null is treated as a valid slug.
    [Fact]
    public void ANullSlugIsNotValid() => Assert.False(ProvisionTenantRequest.IsValidSlug(null));

    private static async Task AssertUntouchedAsync(string database)
    {
        Assert.False(await Db.SchemaExistsAsync(database, "catalog"));
        Assert.Equal(0L, await ProvisionSupport.TenantSchemaCountAsync(database));
    }
}
