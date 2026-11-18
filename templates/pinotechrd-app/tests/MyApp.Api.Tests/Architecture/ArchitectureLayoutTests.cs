namespace MyApp.Api.Tests.Architecture;

/// <summary>
/// Self-tests of the detection that makes the exact assembly counts layout-aware, on synthetic folders and names: the
/// real project only ever shows one of the cases.
/// </summary>
public class ArchitectureLayoutTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void TheSupersetIsRecognizedOnlyWhenTheRulesOfBothArchitecturesExist(bool clean, bool modular, bool superset)
    {
        var project = Directory.CreateTempSubdirectory("layout-").FullName;
        try
        {
            if (clean)
            {
                Directory.CreateDirectory(Path.Combine(project, "Architecture", "Clean"));
            }

            if (modular)
            {
                Directory.CreateDirectory(Path.Combine(project, "Architecture", "Modular"));
            }

            Assert.Equal(superset, ArchitectureLayout.HasBothRuleFolders(project));
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }

    [Fact]
    public void TheRunningProjectHasTheRulesOfAtLeastOneArchitecture()
    {
        var project = Path.Combine(Support.SolutionLocator.SolutionRoot(), "tests", ReferenceRules.SolutionRoot + ".Api.Tests", "Architecture");

        Assert.True(
            Directory.Exists(Path.Combine(project, "Clean")) || Directory.Exists(Path.Combine(project, "Modular")),
            "The detection looks in a folder that holds neither rule folder: it would call every project clean-only or modular-only.");
    }

    [Theory]
    [InlineData("Acme", "Acme.Modules.Sales.Domain", "Sales", "Domain")]
    [InlineData("Acme", "Acme.Modules.SalesOrders.Endpoints", "SalesOrders", "Endpoints")]
    [InlineData("Acme.Demo", "Acme.Demo.Modules.Core.Contracts", "Core", "Contracts")]
    public void AModuleAssemblyNameGivesItsModuleAndLayer(string root, string name, string module, string layer)
    {
        Assert.Equal(new ModuleAssemblyName(module, layer), ModuleAssemblyNames.Parse(name, root));
    }

    [Theory]
    [InlineData("Acme.Modules.Sales")]
    [InlineData("Acme.Modules.Sales.Extras")]
    [InlineData("Acme.Modules.Sales.Domain.Extra")]
    [InlineData("Acme.Modules..Domain")]
    [InlineData("Acme.Modules.Sales.Tests")]
    [InlineData("Acme.Sales.Domain")]
    [InlineData("Other.Modules.Sales.Domain")]
    [InlineData("Acme.ModulesX.Sales.Domain")]
    public void ANameThatIsNotAWellFormedModuleAssemblyIsNotParsed(string name)
    {
        Assert.Null(ModuleAssemblyNames.Parse(name, "Acme"));
    }

    [Fact]
    public void EndpointsAssembliesInAFolderAreOnlyTheEndpointsOfModules()
    {
        var directory = Directory.CreateTempSubdirectory("module-assemblies-").FullName;
        try
        {
            foreach (var file in new[]
            {
                "Acme.Modules.Sales.Endpoints.dll", "Acme.Modules.Billing.Endpoints.dll", "Acme.Modules.Billing.Endpoints.pdb",
                "Acme.Modules.Sales.Domain.dll", "Acme.Modules.Sales.Endpoints.Tests.dll", "Acme.Modules.A.B.Endpoints.dll",
                "Acme.Endpoints.dll", "Other.Modules.Sales.Endpoints.dll",
            })
            {
                File.WriteAllBytes(Path.Combine(directory, file), []);
            }

            Assert.Equal(
                ["Acme.Modules.Billing.Endpoints", "Acme.Modules.Sales.Endpoints"],
                ModuleAssemblyNames.EndpointsAssembliesIn(directory, "Acme"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
