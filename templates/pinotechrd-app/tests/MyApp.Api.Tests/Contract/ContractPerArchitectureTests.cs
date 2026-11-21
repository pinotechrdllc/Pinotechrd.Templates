using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Contract;

// The template source keeps one committed contract per architecture; the template engine ships the one of the chosen
// architecture as contract/openapi.v1.json. These run in the source and in every generated project, and check whichever
// side they are on.
public class ContractPerArchitectureTests
{
    private const string ShippedContract = "contract/" + OpenApiContract.ContractFileName;

    // The placeholder module's name, built from parts: the template engine replaces the joined name in every file it
    // generates, and a clean output must not contain the replacement either.
    private static readonly Regex PlaceholderModule = new(
        string.Join('|', "My" + "Module", "my" + "-module"),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Breaks when the template source loses the contract of an architecture or keeps a plain openapi.v1.json next to
    // them, or when a generated project ships anything but exactly contract/openapi.v1.json.
    [Fact]
    public void TheContractFolderHoldsTheContractsOfWhereTheTestsRun()
    {
        var root = SolutionLocator.SolutionRoot();
        var contracts = Directory.GetFiles(Path.Combine(root, "contract"), "openapi.v1*.json")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Guard: the detection of the template source agrees with what is on disk, so one branch below always runs for
        // the right reason. The source holds the composition of both architectures, an output only its own.
        var compositions = Directory.GetFiles(Path.Combine(root, "src", OpenApiContract.ApiProjectName), "BusinessComposition.*.cs");
        Assert.Equal(SolutionLocator.IsTemplateSource ? 2 : 1, compositions.Length);

        if (SolutionLocator.IsTemplateSource)
        {
            Assert.Equal(OpenApiContract.SourceArchitectures.Select(OpenApiContract.ArchitectureContractFileName).Order(StringComparer.Ordinal), contracts);
            Assert.DoesNotContain(OpenApiContract.ContractFileName, contracts);
        }
        else
        {
            Assert.Equal([OpenApiContract.ContractFileName], contracts);
        }

        Assert.All(OpenApiContract.ContractPaths, p => Assert.True(File.Exists(p), $"{p} does not exist."));
    }

    // Breaks when the modifier of an architecture stops excluding the contract of the other one, excludes its own, or
    // stops renaming its own to contract/openapi.v1.json: the output would ship two contracts, none, or the wrong one.
    [Fact]
    public void EachArchitectureShipsItsOwnContractAsTheContract()
    {
        Assert.SkipUnless(SolutionLocator.IsTemplateSource, "Only the template source has .template.config/template.json.");

        var template = JsonNode.Parse(
            File.ReadAllText(SolutionLocator.TemplateConfigFile),
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
        var modifiers = template["sources"]!.AsArray()
            .SelectMany(s => s!["modifiers"]?.AsArray() ?? [])
            .Select(m => m!.AsObject())
            .ToArray();

        foreach (var architecture in OpenApiContract.SourceArchitectures)
        {
            var own = "contract/" + OpenApiContract.ArchitectureContractFileName(architecture);
            var others = OpenApiContract.SourceArchitectures
                .Where(a => a != architecture)
                .Select(a => "contract/" + OpenApiContract.ArchitectureContractFileName(a))
                .ToArray();
            Assert.NotEmpty(others);

            var modifier = Assert.Single(modifiers, m => m["condition"]?.GetValue<string>() == $"({architecture})");
            var excluded = modifier["exclude"]?.AsArray().Select(e => e!.GetValue<string>()).ToArray() ?? [];
            var renamed = modifier["rename"]?.AsObject().ToDictionary(r => r.Key, r => r.Value?.GetValue<string>()) ?? [];

            Assert.All(others, other => Assert.Contains(other, excluded));
            Assert.DoesNotContain(own, excluded);
            Assert.True(renamed.TryGetValue(own, out var target), $"The ({architecture}) modifier does not rename {own}.");
            Assert.Equal(ShippedContract, target);
            Assert.Single(renamed, r => r.Value == ShippedContract);
            Assert.DoesNotContain(renamed.Keys, others.Contains);
        }
    }

    // Breaks when the modular contract is regenerated with endpoints of the placeholder module: the stamp module ships no
    // endpoints, and its name would leak into the contract of every modular output.
    [Fact]
    public void TheModularContractDoesNotMentionThePlaceholderModule()
    {
        Assert.SkipUnless(SolutionLocator.IsTemplateSource, "Only the template source has the modular contract under its own name.");

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
}
