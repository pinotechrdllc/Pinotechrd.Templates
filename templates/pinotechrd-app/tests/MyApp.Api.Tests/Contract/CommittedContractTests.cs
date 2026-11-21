using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Contract;

// Rules about the committed contract/openapi.v1.json itself, read as a file. In the template source they apply to the
// file of each architecture (OpenApiContract.ContractPaths).
public partial class CommittedContractTests
{
    /// <summary>Every committed contract file, with its name for messages, its text and its document.</summary>
    private static IEnumerable<(string Name, string Path, JsonObject Document)> Committed()
    {
        var paths = OpenApiContract.ContractPaths;
        Assert.NotEmpty(paths);
        return paths.Select(p => (Path.GetFileName(p), p, OpenApiContract.Read(p)));
    }

    [GeneratedRegex(@"localhost|127\.0\.0\.1|0\.0\.0\.0|(?<=[\w\]\.]):\d{2,5}\b", RegexOptions.IgnoreCase)]
    private static partial Regex HostOrPort();

    [GeneratedRegex(@"\b[A-Za-z]:(\\|/)|(^|[\s""'(])/(home|Users|tmp|var|usr|mnt|opt|root|src|app|workspace)/", RegexOptions.Multiline)]
    private static partial Regex AbsolutePath();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}")]
    private static partial Regex Timestamp();

    // Breaks when an operation loses its WithName or two endpoints share a name.
    [Fact]
    public void EveryOperationHasANonEmptyUniqueOperationId()
    {
        foreach (var (name, _, document) in Committed())
        {
            var operations = OpenApiContract.Operations(document);
            Assert.True(operations.Count > 0, $"{name} has no operations.");

            var missing = operations
                .Where(o => string.IsNullOrWhiteSpace(o.Operation["operationId"]?.GetValue<string>()))
                .Select(o => $"{o.Method.ToUpperInvariant()} {o.Path}")
                .ToArray();
            Assert.True(missing.Length == 0, $"{name}: operations without an operationId: " + string.Join(", ", missing));

            var duplicated = operations
                .GroupBy(o => o.Operation["operationId"]!.GetValue<string>(), StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => $"'{g.Key}' on {string.Join(" and ", g.Select(o => o.Path))}")
                .ToArray();
            Assert.True(duplicated.Length == 0, $"{name}: duplicate operationIds: " + string.Join("; ", duplicated));
        }
    }

    // Breaks when an endpoint is mapped outside the versioned prefix.
    [Fact]
    public void EveryPathIsUnderTheVersionedPrefix()
    {
        foreach (var (name, _, document) in Committed())
        {
            var paths = OpenApiContract.Paths(document).Select(p => p.Key).ToArray();
            Assert.True(paths.Length > 0, $"{name} has no paths.");

            var outside = paths.Where(p => !p.StartsWith("/api/v1", StringComparison.Ordinal)).ToArray();
            Assert.True(outside.Length == 0, $"{name}: paths outside /api/v1: " + string.Join(", ", outside));
        }
    }

    // Breaks when the health probes or the document endpoint itself leak into the contract.
    [Fact]
    public void HealthAndOpenApiPathsAreNotInTheContract()
    {
        foreach (var (name, _, document) in Committed())
        {
            var leaked = OpenApiContract.Paths(document).Select(p => p.Key)
                .Where(p => p.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
                    || p.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase)
                    || p.Contains("/health", StringComparison.OrdinalIgnoreCase)
                    || p.Contains("/openapi", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.True(leaked.Length == 0, $"{name}: infrastructure paths in the contract: " + string.Join(", ", leaked));
        }
    }

    // Breaks when the version endpoint is renamed, moved, or stops declaring a JSON 200 response.
    [Fact]
    public void VersionOperationIsDocumentedWithAJsonSchemaForItsOkResponse()
    {
        foreach (var (name, _, document) in Committed())
        {
            var operation = OpenApiContract.Paths(document)["/api/v1/version"]?["get"]?.AsObject();

            Assert.True(operation is not null, $"{name} has no GET /api/v1/version.");
            Assert.Equal("GetVersion", operation["operationId"]?.GetValue<string>());
            var schema = OpenApiContract.ResponseSchema(operation, "200", "application/json");
            Assert.True(schema is not null, $"{name}: GET /api/v1/version has no JSON 200 schema.");
            Assert.NotEmpty(OpenApiContract.ResolveSchema(document, schema));
        }
    }

    // Breaks when the problem schemas lose a required property, widen status, close the code list, or lose errors.
    [Fact]
    public void ProblemSchemasAreRequiredTypedAndOpenForCodes()
    {
        foreach (var (_, _, document) in Committed())
        {
            OpenApiDocumentRules.AssertProblemSchemas(document);
        }
    }

    // Breaks when an operation stops declaring the problem responses its input implies, or declares ones it cannot return.
    [Fact]
    public void EveryOperationDeclaresTheCommonProblemResponses()
    {
        using var factory = new ApiHostFactory();
        var expectations = OpenApiDocumentRules.ExpectationsOf(factory.Services);
        foreach (var (_, _, document) in Committed())
        {
            OpenApiDocumentRules.AssertCommonProblemResponses(document, expectations);
        }
    }

    // Breaks when the file is written with CRLF (a Windows regeneration without normalising line endings).
    [Fact]
    public void ContractFileUsesLineFeedOnly()
    {
        foreach (var (name, path, _) in Committed())
        {
            var bytes = File.ReadAllBytes(path);

            Assert.True(bytes.Length > 0, $"{name} is empty.");
            Assert.True(!bytes.Contains((byte)'\r'), $"{name} contains a carriage return.");
        }
    }

    // Breaks when a machine specific value (path, host, port, timestamp) ends up in the generated file.
    [Fact]
    public void ContractFileHasNoMachineSpecificValues()
    {
        foreach (var (_, path, _) in Committed())
        {
            var text = File.ReadAllText(path);

            Assert.DoesNotMatch(HostOrPort(), text);
            Assert.DoesNotMatch(AbsolutePath(), text);
            Assert.DoesNotMatch(Timestamp(), text);
            Assert.DoesNotContain(SolutionLocator.SolutionRoot(), text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Proves the checks above can fail: they must flag text that a bad regeneration would produce.
    [Theory]
    [InlineData("\"url\": \"http://localhost:5000\"")]
    [InlineData("\"url\": \"https://example.org:8443/x\"")]
    public void HostAndPortPatternFlagsServerUrls(string text) => Assert.Matches(HostOrPort(), text);

    [Theory]
    [InlineData("\"file\": \"C:\\\\src\\\\Api.xml\"")]
    [InlineData("\"file\": \"/home/runner/work/Api.xml\"")]
    public void AbsolutePathPatternFlagsFilePaths(string text) => Assert.Matches(AbsolutePath(), text);

    [Fact]
    public void TimestampPatternFlagsGenerationTimes() => Assert.Matches(Timestamp(), "\"generated\": \"2026-11-17T10:30:00Z\"");

    [Fact]
    public void DescriptionsOfTheCurrentContractAreNotFlaggedByMistake()
    {
        Assert.DoesNotMatch(HostOrPort(), "\"description\": \"Bad Request\"");
        Assert.DoesNotMatch(AbsolutePath(), "\"$ref\": \"#/components/schemas/ProblemDetails\"");
    }

    // Breaks when an operation falls back to the assembly name as its tag, or has none.
    [Fact]
    public void EveryOperationHasATagAndNoneIsTheAssemblyName()
    {
        foreach (var (_, _, document) in Committed())
        {
            OpenApiDocumentRules.AssertTags(document);
            Assert.Equal(["System"], OpenApiContract.Tags(OpenApiContract.Paths(document)["/api/v1/version"]!["get"]!.AsObject()));
        }
    }

    // Breaks when the file ends with a newline, which an editor would add or remove on save.
    [Fact]
    public void ContractFileDoesNotEndWithANewline()
    {
        foreach (var (name, path, _) in Committed())
        {
            var bytes = File.ReadAllBytes(path);

            Assert.True(bytes.Length > 0, $"{name} is empty.");
            Assert.True(bytes[^1] != (byte)'\n', $"{name} ends with a newline.");
        }
    }

    // Breaks when the editorconfig stops telling editors not to add a final newline to contract files.
    [Fact]
    public void EditorConfigTurnsOffTheFinalNewlineForContractFiles()
    {
        var lines = File.ReadAllLines(Path.Combine(SolutionLocator.SolutionRoot(), ".editorconfig"));

        var start = Array.FindIndex(lines, l => l.Trim() == "[contract/*.json]");
        Assert.True(start >= 0, "The .editorconfig has no [contract/*.json] section.");
        var section = lines.Skip(start + 1).TakeWhile(l => !l.TrimStart().StartsWith('[')).ToArray();

        Assert.Contains(section, l => FinalNewlineOff().IsMatch(l));
    }

    [GeneratedRegex(@"^\s*insert_final_newline\s*=\s*false\s*(#.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex FinalNewlineOff();
}
