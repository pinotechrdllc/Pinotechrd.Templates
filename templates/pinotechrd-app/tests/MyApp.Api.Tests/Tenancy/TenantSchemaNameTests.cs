using System.Text.RegularExpressions;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;

namespace MyApp.Api.Tests.Tenancy;

// The rule that names a tenant's schema and the check every schema name passes before it reaches SQL. No database.
public class TenantSchemaNameTests
{
    private static readonly Regex Documented = new("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant);

    // Breaks when two tenants can get the same schema (a truncated, lower-cased or otherwise lossy derivation), when a
    // derived name is not a valid identifier, or when it can exceed PostgreSQL's 63 characters.
    [Fact]
    public void SchemaNameIsInjectiveOverManyRandomIdsAlwaysValidAndAtMost63Characters()
    {
        var ids = Enumerable.Range(0, 20_000).Select(_ => Guid.NewGuid()).ToList();

        var names = ids.Select(TenantSchemaName.For).ToList();

        Assert.Equal(ids.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, name =>
        {
            Assert.True(TenantSchemaName.IsValid(name), $"'{name}' is not valid.");
            Assert.Matches(Documented, name);
            Assert.True(name.Length <= 63, $"'{name}' has {name.Length} characters.");
            Assert.StartsWith("tenant_", name, StringComparison.Ordinal);
        });
        Assert.Equal(ids.Count, names.Select(n => n.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Count());
    }

    // Breaks when ids that differ in one bit give the same schema.
    [Fact]
    public void IdsThatDifferInOneByteGiveDifferentSchemas()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        var names = new HashSet<string>(StringComparer.Ordinal) { TenantSchemaName.For(new Guid(bytes)) };
        for (var i = 0; i < bytes.Length; i++)
        {
            var changed = (byte[])bytes.Clone();
            changed[i] ^= 0x01;
            Assert.True(names.Add(TenantSchemaName.For(new Guid(changed))), $"Flipping a bit of byte {i} kept the schema.");
        }
    }

    // Breaks when the empty id (which would give one shared, guessable schema) is accepted.
    [Fact]
    public void TheEmptyIdIsRejected()
    {
        Assert.Throws<ArgumentException>(() => TenantSchemaName.For(Guid.Empty));
    }

    public static readonly string?[] HostileNames =
        [
            null,
            string.Empty,
            "bad name",
            "Tenant_UPPER",
            "evil;drop",
            "evil\"quote",
            "evil'quote",
            "evil--comment",
            "9leading_digit",
            "_leading_underscore",
            "tenänt_unicode",
            "tenant_١٢",
            new string('a', 64),
            "tenant_trailing_newline\n",
            "tenant\tx",
            "public.other",
            "tenant-dash",
        ];

    public static TheoryData<string?> Hostile() => [.. HostileNames];

    // Breaks when a name outside ^[a-z][a-z0-9_]{0,62}$ passes (quotes, semicolons, upper case, spaces, empty, 64
    // characters, leading digit, Unicode, a trailing line feed that a '$' anchor would let through).
    [Theory]
    [MemberData(nameof(Hostile))]
    public void HostileSchemaNamesAreRejected(string? name)
    {
        Assert.False(TenantSchemaName.IsValid(name));
        Assert.Throws<ArgumentException>(() => TenantSchemaName.Validate(name));
    }

    // Breaks when the error message echoes the rejected value (it could be hostile text, or a secret).
    [Theory]
    [InlineData("bad name")]
    [InlineData("evil;drop")]
    [InlineData("evil\"quote")]
    [InlineData("Tenant_UPPER")]
    [InlineData("9leading_digit")]
    [InlineData("tenänt_unicode")]
    [InlineData("tenant_trailing_newline\n")]
    public void TheErrorMessageDoesNotEchoTheRejectedValue(string name)
    {
        var error = Assert.Throws<ArgumentException>(() => TenantSchemaName.Validate(name));

        Assert.DoesNotContain(name, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(name.Trim(), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMessageForA64CharacterNameDoesNotEchoIt()
    {
        var name = new string('q', 64);

        var error = Assert.Throws<ArgumentException>(() => TenantSchemaName.Validate(name));

        Assert.DoesNotContain("qqqqqqqq", error.Message, StringComparison.Ordinal);
    }

    // Breaks when a plain valid name is refused.
    [Theory]
    [InlineData("a")]
    [InlineData("a_1")]
    [InlineData("tenant_0f8fad5bd9cb469fa16570867728950e")]
    public void ValidNamesPass(string name)
    {
        Assert.True(TenantSchemaName.IsValid(name));
        Assert.Equal(name, TenantSchemaName.Validate(name));
    }

    // Breaks when the boundaries of the pattern move: 63 characters are valid, 64 are not.
    [Fact]
    public void ALengthOf63IsTheLongestValidName()
    {
        var longest = "a" + new string('b', 62);

        Assert.Equal(63, longest.Length);
        Assert.True(TenantSchemaName.IsValid(longest));
        Assert.False(TenantSchemaName.IsValid(longest + "b"));
    }

    // Breaks when the documented pattern constant drifts from the rule the README states.
    [Fact]
    public void ThePublishedPatternIsTheDocumentedOne()
    {
        Assert.Equal("^[a-z][a-z0-9_]{0,62}$", TenantSchemaName.Pattern);
    }
}
