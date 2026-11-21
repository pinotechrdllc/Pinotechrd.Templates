using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Contract;

// Rules that only the template source can check: .template.config/template.json excludes this file from every output,
// so these assert the source unconditionally and never skip.
public partial class TemplateSourceContractTests
{
    private const string ShippedContract = "contract/" + OpenApiContract.ContractFileName;

    // The MSBuild property that picks one architecture of the source, built from parts like in OpenApiContract.
    private static readonly string ArchitectureProperty = "Contract" + "Architecture";

    // The placeholder module's name, built from parts: the template engine replaces the joined name in every file it
    // generates, and a clean output must not contain the replacement either.
    private static readonly Regex PlaceholderModule = new(
        string.Join('|', "My" + "Module", "my" + "-module"),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [GeneratedRegex(@"^\s*'\$\((?<property>\w+)\)'\s*==\s*'(?<value>[^']*)'\s*$")]
    private static partial Regex PropertyEquals();

    // Guard: these rules read the files of the source, so they must be running in it.
    [Fact]
    public void TheTestsRunInTheTemplateSource()
    {
        Assert.True(SolutionLocator.IsTemplateSource,
            $"{nameof(TemplateSourceContractTests)} only exists in the template source, but the source is not detected: "
            + $"it needs {SolutionLocator.TemplateConfigFile} and {OpenApiContract.ContractDisplayName}.");
    }

    // Breaks when the modifier of an architecture stops excluding the contract of the other one, excludes its own, or
    // stops renaming its own to contract/openapi.v1.json: the output would ship two contracts, none, or the wrong one.
    [Fact]
    public void EachArchitectureShipsItsOwnContractAsTheContract()
    {
        var modifiers = Modifiers();

        foreach (var architecture in OpenApiContract.SourceArchitectures)
        {
            var own = "contract/" + OpenApiContract.ArchitectureContractFileName(architecture);
            var others = OpenApiContract.SourceArchitectures
                .Where(a => a != architecture)
                .Select(a => "contract/" + OpenApiContract.ArchitectureContractFileName(a))
                .ToArray();
            Assert.NotEmpty(others);

            var modifier = ModifierOf(modifiers, architecture);
            var excluded = Excluded(modifier);
            var renamed = modifier["rename"]?.AsObject().ToDictionary(r => r.Key, r => r.Value?.GetValue<string>()) ?? [];

            Assert.All(others, other => Assert.Contains(other, excluded));
            Assert.DoesNotContain(own, excluded);
            Assert.True(renamed.TryGetValue(own, out var target), $"The ({architecture}) modifier does not rename {own}.");
            Assert.Equal(ShippedContract, target);
            Assert.Single(renamed, r => r.Value == ShippedContract);
            Assert.DoesNotContain(renamed.Keys, others.Contains);
        }
    }

    // Breaks when a file of the API project that exists in one architecture only is excluded from an output by
    // template.json but still compiled by the source's contract run of the other architecture (or the reverse): the
    // source would then regenerate a contract that the output of that architecture does not serve.
    [Fact]
    public void TheApiFilesExcludedPerArchitectureAreTheOnesTheContractRunRemoves()
    {
        var modifiers = Modifiers();
        var prefix = $"src/{OpenApiContract.ApiProjectName}/";
        var project = XDocument.Load(Path.Combine(SolutionLocator.SolutionRoot(), "src", OpenApiContract.ApiProjectName, OpenApiContract.ApiProjectName + ".csproj"));

        foreach (var architecture in OpenApiContract.SourceArchitectures)
        {
            var excluded = Excluded(ModifierOf(modifiers, architecture))
                .Where(e => e.StartsWith(prefix, StringComparison.Ordinal))
                .Select(e => e[prefix.Length..])
                .Order(StringComparer.Ordinal)
                .ToArray();

            // The template-condition comments around the block are XML comments, so the parser skips them.
            var group = Assert.Single(
                project.Descendants("ItemGroup"),
                g => PropertyEquals().Match(g.Attribute("Condition")?.Value ?? string.Empty) is { Success: true } m
                    && m.Groups["property"].Value == ArchitectureProperty
                    && m.Groups["value"].Value == architecture);
            var removed = group.Elements("Compile")
                .Select(c => c.Attribute("Remove")?.Value)
                .OfType<string>()
                .Select(r => r.Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.NotEmpty(excluded);
            Assert.Equal(excluded, removed);
        }
    }

    // Breaks when the modular contract is regenerated with endpoints of the placeholder module: the stamp module ships no
    // endpoints, and its name would leak into the contract of every modular output.
    [Fact]
    public void TheModularContractDoesNotMentionThePlaceholderModule()
    {
        var text = File.ReadAllText(SolutionLocator.ContractFile(OpenApiContract.ArchitectureContractFileName("modular")));

        Assert.NotEmpty(text);
        Assert.DoesNotMatch(PlaceholderModule, text);
    }

    // Proves the check above can fail: a route or a schema of the placeholder module is flagged.
    [Fact]
    public void PlaceholderModulePatternFlagsItsRoutesAndSchemas()
    {
        Assert.Matches(PlaceholderModule, "\"/api/v1/" + "my" + "-module/items\"");
        Assert.Matches(PlaceholderModule, "\"#/components/schemas/" + "My" + "ModuleItem\"");
        Assert.DoesNotMatch(PlaceholderModule, "\"/api/v1/version\"");
    }

    // Proves the csproj condition is read as the property and the value it compares, and nothing else.
    [Fact]
    public void PropertyConditionPatternReadsThePropertyAndTheValue()
    {
        var match = PropertyEquals().Match($"'$({ArchitectureProperty})' == 'clean'");

        Assert.True(match.Success);
        Assert.Equal(ArchitectureProperty, match.Groups["property"].Value);
        Assert.Equal("clean", match.Groups["value"].Value);
        Assert.DoesNotMatch(PropertyEquals(), $"'$({ArchitectureProperty})' != 'clean'");
    }

    private static JsonObject[] Modifiers()
    {
        var template = JsonNode.Parse(
            File.ReadAllText(SolutionLocator.TemplateConfigFile),
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
        return [.. template["sources"]!.AsArray()
            .SelectMany(s => s!["modifiers"]?.AsArray() ?? [])
            .Select(m => m!.AsObject())];
    }

    private static JsonObject ModifierOf(JsonObject[] modifiers, string architecture) =>
        Assert.Single(modifiers, m => m["condition"]?.GetValue<string>() == $"({architecture})");

    private static string[] Excluded(JsonObject modifier) =>
        modifier["exclude"]?.AsArray().Select(e => e!.GetValue<string>()).ToArray() ?? [];
}
