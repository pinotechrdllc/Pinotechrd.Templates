using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Contract;

// The template source keeps one committed contract per architecture; the template engine ships the one of the chosen
// architecture as contract/openapi.v1.json. This runs in the source and in every generated project, and checks whichever
// side it is on. The rules that only the source can check are in TemplateSourceContractTests, which no output ships.
public class ContractPerArchitectureTests
{
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
}
